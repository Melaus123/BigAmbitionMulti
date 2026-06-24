# BigAmbitionsMP bug-report relay

A tiny Cloudflare Worker that receives bug reports from the mod and forwards them
to your Discord bug forum. **It exists so the Discord webhook never ships inside
the mod** — the webhook lives only here, on Cloudflare, as an encrypted secret.

```
   Player's game ──POST report──▶  this Worker (holds the webhook)  ──▶  Discord forum
   (knows only the public            (https://...workers.dev)
    Worker URL, never the webhook)
```

Players configure **nothing**. The Worker URL is baked into the mod; reports
submit automatically. You configure the webhook **once**, here.

---

## 1. Get your Discord webhook (one time)

On your Discord server, on the **bug forum channel**:
Edit Channel → **Integrations → Webhooks → New Webhook** → name it (e.g. "BAMP
Reports") → **Copy Webhook URL**. It looks like:
`https://discord.com/api/webhooks/000.../abc...`

Treat that URL as a password — anyone who has it can post to that channel. You'll
paste it into Cloudflare (step 3), and it will never leave Cloudflare.

> Tip: add `?wait=true` to the end of the webhook URL so Discord confirms each
> post — the relay then reports real success/failure back to the mod.

## 2. Create the Worker

**Easiest — no command line (Cloudflare dashboard):**
1. Sign up / log in at https://dash.cloudflare.com (free).
2. **Workers & Pages → Create → Create Worker**. Give it a name (e.g.
   `bamp-bug-relay`) and Deploy the starter.
3. **Edit code**, delete the starter, paste the contents of [`worker.js`](worker.js), **Save and Deploy**.
4. Your URL is shown at the top, e.g. `https://bamp-bug-relay.<your-subdomain>.workers.dev`.

**Or with the CLI (Wrangler):**
```bash
npm i -g wrangler        # or use: npx wrangler ...
wrangler login
cd relay
wrangler deploy          # prints your workers.dev URL
```

## 3. Add the webhook as a secret

**Dashboard:** open the Worker → **Settings → Variables and Secrets → Add** →
name `DISCORD_WEBHOOK_URL`, type **Secret**, value = your webhook URL from step 1
→ Save → **Deploy** again so it takes effect.

**CLI:** `wrangler secret put DISCORD_WEBHOOK_URL` then paste the URL.

That's the minimum. The relay now works.

## 4. (Optional, recommended) lock it down a bit more

- **Shared key** — add a second secret `RELAY_KEY` = any random string. Tell me
  that string; I'll have the mod send it in a header so the public URL ignores
  random POSTs. (It ships in the mod so it's not a true secret, but it stops
  casual abuse.)
- **Rate limit** — either add a Cloudflare **Rate limiting rule** on the Worker
  route (dashboard, no code), or create a KV namespace and bind it as `RL` (see
  [`wrangler.toml`](wrangler.toml)); the Worker then caps each IP to 8 reports /
  10 min.

## 5. Send me the URL

Give me the `https://...workers.dev` URL (and the `RELAY_KEY` if you set one) and
I'll bake it into the mod and switch bug submission to the relay. Test by filing
a report in-game — it should appear as a new thread in your Discord forum.

---

## Why this is safe

- The **webhook secret is only on Cloudflare**, never in the mod or its config,
  so it can't be extracted from a downloaded build.
- The worst anyone can do with the public Worker URL is *send a report* — which
  you moderate — and that's throttled by the key + rate limit.
- **Rotate** the webhook anytime: update the `DISCORD_WEBHOOK_URL` secret and
  redeploy. No mod release needed.
- The mod still strips IPv4 addresses from report text **before** sending, so
  neither Cloudflare nor Discord ever sees a player's/host's IP.
