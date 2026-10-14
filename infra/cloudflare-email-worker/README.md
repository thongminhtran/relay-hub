# RelayHub — Cloudflare Email Worker

Turns an email sent to a Cloudflare-managed address (e.g. `hello@yourdomain.com`) into a signed `POST /api/v1/hooks/email`.
Cloudflare Email Routing receives the mail, this Worker parses it (`postal-mime`, bundled by wrangler), signs the JSON body
with HMAC-SHA256 and posts it to the RelayHub API. Nothing is stored here; the API queues it and the RelayHub worker stores it.

Payload: `{ to, from, fromName, subject, text, html, messageId, inReplyTo, receivedAt }`, header `X-RelayHub-Signature: sha256=<hex>`.

## Deploy (once per Cloudflare zone)

```bash
cd infra/cloudflare-email-worker
npm install
npx wrangler login
openssl rand -hex 32                                   # the inbound secret
npx wrangler secret put RELAYHUB_INBOUND_SECRET        # paste it
npx wrangler deploy
```

Give the same secret to the API: GitHub secret `EMAIL_INBOUND_SECRET` (Key Vault `RelayHub--Email--InboundSecret` → setting
`RelayHub:Email:InboundSecret`). Then in the Cloudflare dashboard: **Email → Email Routing → Routing rules** → create an address
(e.g. `hello@yourdomain.com`) with action **Send to a Worker → relayhub-email-worker**. Email Routing must be enabled on the zone
(it adds the MX records).

In RelayHub, create a source of type `email` whose `externalAccountId` is that address (lower-case); the worker matches on it. A
workspace with exactly one enabled email source also receives mail for any address (catch-all).

`FORWARD_TO` (optional var in `wrangler.toml`): also forward every mail to a verified destination mailbox. When it is set, an API
failure is logged instead of bouncing the mail (you still have the copy); without it the Worker throws, so the sender's mail server
retries later and nothing is lost silently.

## Test without sending a real mail

```bash
BODY='{"to":"hello@yourdomain.com","from":"anna@example.com","fromName":"Anna","subject":"Test","text":"Hello","messageId":"<test-1@example.com>"}'
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$RELAYHUB_INBOUND_SECRET" | awk '{print $NF}')
curl -i -X POST "$RELAYHUB_API_URL/api/v1/hooks/email" -H "Content-Type: application/json" -H "X-RelayHub-Signature: sha256=$SIG" --data "$BODY"
```

Expect `202 {"status":"queued", ...}`; a few seconds later the event appears in the inbox.
