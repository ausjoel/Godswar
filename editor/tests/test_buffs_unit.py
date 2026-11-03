import sys,unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from modules import buffs,common

class Context:
 def __init__(self,body=None):
  self.body=body or {};self.rb=SimpleNamespace(writable=True,pg=SimpleNamespace(json=lambda q:{'id':7,'name':'PortalAth'}));self.catalog=common.CatalogService();self.calls=[]
  self.caps={'port':51090,'operations':{'character.buff_apply':{'path':'/admin/buff/apply'}}}
 def need_rb(self):return self.rb
 def live_call(self,*args,**kw):self.calls.append((args,kw));return {'ok':True,'result':{'applied':True}}

def catalogue():return {'version':1,'maxDurationSeconds':2**32-1,'buffs':[{'skillId':774,'statusId':274,'kind':34,'priority':5,'beneficial':True,'durationSeconds':10,'effects':{'dodge':3000,'criticalResistance':1000}}]}

class BuffEditorTests(unittest.TestCase):
 def test_legacy_server_disables_tools_without_probe(self):
  ctx=Context();ctx.caps['operations']={}
  with patch.object(common,'_http') as http:self.assertFalse(buffs.runtime_catalogue(ctx)['supported']);http.assert_not_called()
 def test_catalogue_uses_actual_native_names_and_descriptions(self):
  with patch.object(common,'_http',return_value=(200,catalogue())):
   row=buffs.runtime_catalogue(Context())['buffs'][0]
  self.assertEqual(row['name'],'Gaia Care 5');self.assertIn('Dodge',row['description']);self.assertEqual(row['effects']['dodge'],3000)
 def test_invalid_runtime_catalogue_refused(self):
  for result in [(404,{}),(200,{'version':2,'buffs':[]}),(200,{'version':1,'buffs':'bad'})]:
   with self.subTest(result=result),patch.object(common,'_http',return_value=result),self.assertRaises(common.Bad):buffs.runtime_catalogue(Context())
 def test_unknown_and_invalid_durations_never_apply(self):
  for body in [{'skill_id':999},{'skill_id':774,'duration_seconds':0},{'skill_id':774,'duration_seconds':True},{'skill_id':774,'duration_seconds':2**32},{'skill_id':774,'duration_seconds':1.5}]:
   ctx=Context(body)
   with self.subTest(body=body),patch.object(common,'_http',return_value=(200,catalogue())),self.assertRaises(common.Bad):buffs.apply(ctx,'7')
   self.assertFalse(ctx.calls)
 def test_exact_url_target_and_native_duration_reach_runtime(self):
  ctx=Context({'skill_id':774,'duration_seconds':2**32-1,'character':'wrong target'})
  with patch.object(common,'_http',return_value=(200,catalogue())):self.assertEqual(buffs.apply(ctx,'7')['applies'],'live')
  args,kw=ctx.calls[0];self.assertEqual(args,('character.buff_apply','PortalAth',{'skillId':774,'durationSeconds':2**32-1}));self.assertEqual(kw['cid'],7)
 def test_readonly_and_offline_have_no_database_fallback(self):
  ctx=Context({'skill_id':774});ctx.rb.writable=False
  with self.assertRaises(common.Bad):buffs.apply(ctx,7)
  ctx.rb.writable=True;ctx.live_call=lambda *a,**k:{'ok':False,'offline':True}
  with patch.object(common,'_http',return_value=(200,catalogue())),self.assertRaisesRegex(common.Bad,'logged into'):buffs.apply(ctx,7)
 def test_live_audit_is_bound_to_character_and_honest_about_undo(self):
  records=[];app=SimpleNamespace(reborn=SimpleNamespace(db='disposable'),record_reborn_change=records.append)
  ctx=common.Ctx(app,'POST','buffs/apply/7',{},{});caps={'port':51090,'operations':{'character.buff_apply':{'path':'/admin/buff/apply'}}}
  with patch.object(common,'capabilities',return_value=caps),patch.object(common,'_http',return_value=(200,{'applied':True})) as http:
   ctx.live_call('character.buff_apply','PortalAth',{'skillId':774},cid=7,no_undo='Session effect')
  self.assertEqual(records[0]['cid'],7);self.assertEqual(records[0]['no_undo'],'Session effect');self.assertEqual(http.call_args.args[3]['character'],'PortalAth')
if __name__=='__main__':unittest.main(verbosity=2)
