// BigAmbitionsMP bug-report relay (Cloudflare Worker).
//
// Purpose: keep the Discord webhook SECRET server-side. The mod POSTs each
// bug report to this Worker's public URL; the Worker forwards it to the
// Discord webhook (stored as an encrypted Worker secret) and returns Discord's
// status. The webhook is never shipped in the mod, so it can't be extracted or
// abused, and you can rotate it any time without re-releasing the mod.
//
// The mod sends exactly the multipart/form-data Discord expects (payload_json
// + files[]), so this Worker is a transparent proxy — it doesn't parse the
// report, it just streams it through after a few cheap safety checks.
//
// Secrets / vars (set in the Cloudflare dashboard or via wrangler):
//   DISCORD_WEBHOOK_URL  (required, secret) — your forum-channel webhook URL.
//   RELAY_KEY            (optional, secret) — shared string the mod must send
//                        in the X-BAMP-Key header. Obfuscation, not true
//                        security (it ships in the mod), but it stops casual
//                        drive-by abuse of the public URL.
//   RL                   (optional, KV namespace binding) — enables a simple
//                        per-IP rate limit (8 reports / 10 min). Without it,
//                        rely on a Cloudflare rate-limiting rule instead.

// Relay size ceiling. 25 MiB by default: a 12 MB report was accepted by Discord on this
// server (relay log 2026-09-23), so the webhook limit here is above 10 MiB. Set a MAX_BYTES
// variable in the dashboard to change it. A report over the ceiling is refused here with a
// clear reason instead of being forwarded for Discord to refuse.
const DEFAULT_MAX_BYTES = 25 * 1024 * 1024;
const RL_MAX = 8;                     // reports per window per IP (when KV bound)
const RL_WINDOW_SECONDS = 600;

export default {
  async fetch(request, env, ctx) {
    if (request.method !== "POST") {
      return new Response("This is the BigAmbitionsMP bug-report relay. POST only.", { status: 405 });
    }

    if (!env.DISCORD_WEBHOOK_URL) {
      return new Response("relay not configured (missing DISCORD_WEBHOOK_URL secret)", { status: 500 });
    }

    // Optional shared-key gate.
    if (env.RELAY_KEY && request.headers.get("x-bamp-key") !== env.RELAY_KEY) {
      return new Response("forbidden", { status: 403 });
    }

    // Cheap size guard before buffering.
    const MAX_BYTES = Number(env.MAX_BYTES) > 0 ? Number(env.MAX_BYTES) : DEFAULT_MAX_BYTES;
    const declared = Number(request.headers.get("content-length") || "0");
    if (declared > MAX_BYTES) {
      return new Response("payload too large: " + declared + " bytes, relay limit " + MAX_BYTES, { status: 413 });
    }

    // Optional per-IP rate limit (only if a KV namespace is bound as RL).
    if (env.RL) {
      const ip = request.headers.get("cf-connecting-ip") || "unknown";
      const key = "rl:" + ip;
      const count = Number((await env.RL.get(key)) || "0");
      if (count >= RL_MAX) {
        return new Response("rate limited — try again later", { status: 429 });
      }
      ctx.waitUntil(env.RL.put(key, String(count + 1), { expirationTtl: RL_WINDOW_SECONDS }));
    }

    // Buffer the body (capped) and forward to Discord with the same content-type.
    let body;
    try {
      body = await request.arrayBuffer();
    } catch {
      return new Response("could not read body", { status: 400 });
    }
    if (body.byteLength > MAX_BYTES) {
      return new Response("payload too large: " + body.byteLength + " bytes, relay limit " + MAX_BYTES, { status: 413 });
    }

    const contentType = request.headers.get("content-type") || "application/octet-stream";
    let upstream;
    try {
      upstream = await fetch(env.DISCORD_WEBHOOK_URL, {
        method: "POST",
        headers: { "content-type": contentType },
        body,
      });
    } catch (e) {
      return new Response("relay upstream error: " + (e && e.message ? e.message : "unknown"), { status: 502 });
    }

    // Pass Discord's status back so the mod can show success/failure. On a failure also
    // pass a SHORT excerpt of Discord's error body (e.g. {"message":"Request entity too
    // large","code":40005}) so the mod's log names the real reason, plus Retry-After on a
    // rate limit. The excerpt is capped and anything shaped like a webhook address is
    // blanked, so the secret can never leave through this path.
    const ok = upstream.status >= 200 && upstream.status < 300;
    if (ok) return new Response("ok", { status: upstream.status });
    let detail = "";
    try {
      detail = (await upstream.text()).slice(0, 300).replace(/https?:\/\/[^\s"']*webhooks\/[^\s"']*/gi, "[webhook]");
    } catch { }
    const headers = {};
    const retryAfter = upstream.headers.get("retry-after");
    if (retryAfter) headers["retry-after"] = retryAfter;
    return new Response("discord returned " + upstream.status + (detail ? ": " + detail : ""), { status: upstream.status, headers });
  },
};
