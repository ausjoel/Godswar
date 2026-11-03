"""Live application of authored gameplay buffs; never writes offline stat rows."""
import re
from . import common
from .common import Bad, NotFound

M=common.module('buffs','Buffs',icon={'sprite':17},order=45,status='ready',
    description='Apply authored class buffs to an online character, using native effects and stacking.',
    needs_character=True)

def runtime_catalogue(ctx):
    caps=ctx.caps
    if 'character.buff_apply' not in (caps.get('operations') or {}):
        return {'supported':False,'buffs':[],'reason':'The game server needs the live buff tools update.'}
    code,doc=common._http('GET',caps['port'],'/admin/buffs/catalog',timeout=3)
    if code!=200 or not isinstance(doc,dict) or doc.get('version')!=1 or not isinstance(doc.get('buffs'),list):
        raise Bad('The running buff catalogue is unavailable; try again after checking the server.')
    rows=[]
    for definition in doc['buffs']:
        row=dict(definition);entry=ctx.catalog.entry('skill',row['skillId']) or {};status=ctx.catalog.entry('status',row['statusId']) or {}
        row.update(name=entry.get('name') or 'Skill #'+str(row['skillId']),
                   description=status.get('note') or entry.get('desc') or '',
                   skill_description=entry.get('desc') or '',rank=entry.get('level'),
                   status_name=status.get('name') or 'Status #'+str(row['statusId']))
        rows.append(row)
    return dict(doc,supported=True,buffs=rows)

def library(ctx):
    """Every status effect the game data defines (build_buff_library.py), each marked with whether the RUNNING server can
    apply it to a player today: only effects in the server's own catalogue (GET /admin/buffs/catalog) can, so the others are
    listed with their labels but cannot be applied, and the page says so."""
    lib=ctx.catalog.file('buff-library')
    if not lib.get('entries'):raise Bad('The buff library file (catalog-origin/buff-library.json) is missing; run build_buff_library.py.')
    try:live=runtime_catalogue(ctx)
    except Bad:live={'supported':False,'buffs':[]}
    by_status={r['statusId']:r for r in live.get('buffs',[]) if 'statusId' in r}
    rows=[]
    for e in lib['entries']:
        row=dict(e);apply=by_status.get(e['id'])
        row['apply_skill_id']=apply['skillId'] if apply else None
        rows.append(row)
    return {'version':lib.get('version'),'source':lib.get('source'),'count':len(rows),'categories':lib.get('categories',[]),
            'applicable':sum(1 for r in rows if r['apply_skill_id'] is not None),'server_supported':bool(live.get('supported')),
            'entries':rows}

@M.get('library')
def library_view(ctx):
    return library(ctx)

def character(ctx,cid):
    cid=common.to_int(cid,1,2**31-1,'character ID')
    row=ctx.need_rb().pg.json("(SELECT json_build_object('id',id,'name',name) FROM character_base WHERE id=%d AND lifecycle_state='active')"%cid)
    if not row:raise NotFound('No active character #'+str(cid))
    return cid,row

@M.get(r'sheet/(\d+)')
def sheet(ctx,cid):
    cid,row=character(ctx,cid)
    return {'character':row,'online':common.online_info(ctx.app,cid),'catalogue':runtime_catalogue(ctx),'writable':bool(ctx.rb.writable)}

@M.post(r'apply/(\d+)')
def apply(ctx,cid):
    if not ctx.need_rb().writable:raise Bad('This editor connection is read-only.')
    cid,row=character(ctx,cid);catalogue=runtime_catalogue(ctx)
    if not catalogue['supported']:raise Bad(catalogue['reason'])
    skill=common.to_int(ctx.body.get('skill_id'),1,2**31-1,'buff skill')
    if skill not in {r['skillId'] for r in catalogue['buffs']}:raise Bad('Choose an authored buff from the running server catalogue.')
    body={'skillId':skill}
    if 'duration_seconds' in ctx.body:
        body['durationSeconds']=common.to_int(ctx.body['duration_seconds'],1,catalogue['maxDurationSeconds'],'duration in seconds')
    result=ctx.live_call('character.buff_apply',row['name'],body,cid=cid,
        no_undo='This live session effect expires naturally or when the player logs out.')
    if result.get('offline'):raise Bad(row['name']+' must be logged into the game to receive a session buff.')
    result.update(applies='live',applies_text=common.APPLIES_TEXT['live'])
    return result


@M.get(r'active/(\d+)')
def active(ctx,cid):
    cid,row=character(ctx,cid);caps=ctx.caps
    if 'character.buff_inspect' not in (caps.get('operations') or {}):
        return {'supported':False,'character':row,'reason':'Active-effect controls need the compatible server update.'}
    code,doc=common._http('POST',caps['port'],'/admin/buff/inspect',{'actor':'gwadmin-web','character':row['name']},timeout=8)
    if code==409 and isinstance(doc,dict) and doc.get('error')=='offline':
        return {'supported':True,'online':False,'character':row,'buffs':[]}
    if code!=200 or not isinstance(doc,dict) or not doc.get('ok'):
        raise common.Conflict('The live effects are unavailable or the character is changing sessions. Refresh shortly.')
    for effect in doc.get('buffs',[]):
        status=ctx.catalog.entry('status',effect['statusId']) or {}
        effect['name']=status.get('name') or 'Status #'+str(effect['statusId'])
    return dict(doc,supported=True,online=True,character=row)

@M.post(r'remove/(\d+)')
def remove(ctx,cid):
    if not ctx.need_rb().writable:raise Bad('This editor connection is read-only.')
    cid,row=character(ctx,cid);expected=ctx.body.get('expected')
    if not isinstance(expected,dict):raise Bad('The displayed effect token is required.')
    epoch=expected.get('epoch');revision=expected.get('revision')
    if not isinstance(epoch,str) or not re.fullmatch('[0-9a-fA-F]{32}',epoch) or int(epoch,16)==0:
        raise Bad('The status lifetime token is invalid.')
    if not isinstance(revision,str) or not re.fullmatch('[0-9]{1,19}',revision) or not 1<=int(revision)<=2**63-1:
        raise Bad('The exact displayed revision string is required.')
    payload={'epoch':epoch,'revision':revision,
        'kind':common.to_int(expected.get('kind'),1,2**31-1,'effect kind'),
        'statusId':common.to_int(expected.get('statusId'),1,2**31-1,'status ID')}
    result=ctx.live_call('character.buff_remove',row['name'],payload,cid=cid,
        no_undo='This removes a live session effect; reapply the effect if required.')
    if result.get('offline'):raise common.Conflict('The character left the game; refresh active effects.')
    result.update(applies='live',applies_text=common.APPLIES_TEXT['live'])
    return result
