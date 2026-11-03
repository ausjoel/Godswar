# GodsWar sign-up site

Players create an account (username, password, e-mail), and the site writes it to the server's
database. Small Python app (standard library + jinja2 + psycopg2) behind Caddy for HTTPS.

## Install (on the server)
```bash
sudo ./install.sh --live --origin https://signup.example.com
```
Needs a domain pointing at the server and ports 80/443 open. `install.sh --help` explains the
options. It creates a least-privilege database role, a systemd unit and the Caddy config
(`GW_SIGNUP_DOMAIN` and `GW_SERVER_IP` come from the environment).

Close sign-ups at any time with `touch /etc/gwsignup/closed`.
E-mail verification needs an SMTP provider; without one, addresses are stored unverified.

Tests: `test/` (run against a throwaway database, never a real one).
