# Sign-up website and editor (optional)

Both need a domain name pointing at your server and ports 80/443 open.

## Sign-up website (`signup/`)
Players create accounts and reset passwords. Install with
`sudo signup/install.sh --live --origin https://signup.example.com`
(read `signup/README.md` first; it explains the database role it creates and Caddy HTTPS).
Set `GW_SIGNUP_DOMAIN` and `GW_SERVER_IP` in Caddy's environment for `signup/caddy/Caddyfile`.

## Editor (`editor/`)
A web tool for characters, items, rates and accounts. It is for **administrators only**; keep
it behind HTTPS and a strong password. Read `editor/README.md`. Set `GODSWAR_EDITOR_URL` for
`OPEN-EDITOR.command`.
