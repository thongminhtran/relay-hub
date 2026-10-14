// RelayHub inbound email: Cloudflare Email Routing → this Worker → POST /api/v1/hooks/email (signed).
// The API verifies the signature and queues the mail; the RelayHub worker turns it into a conversation + event.
import PostalMime from "postal-mime";

const MAX_TEXT_CHARS = 16_000; // the hook accepts 32 KB bodies; keep room for html/subject/headers

export default {
  async email(message, env, ctx) {
    if (!env.RELAYHUB_API_URL || !env.RELAYHUB_INBOUND_SECRET) {
      throw new Error("RELAYHUB_API_URL (var) and RELAYHUB_INBOUND_SECRET (secret) must be configured");
    }

    const raw = new Uint8Array(await new Response(message.raw).arrayBuffer());
    const mail = await new PostalMime().parse(raw);

    const text = (mail.text ?? "").trim();
    const payload = {
      to: message.to,
      from: mail.from?.address ?? message.from,
      fromName: mail.from?.name || null,
      subject: mail.subject ?? "",
      text: clip(text, MAX_TEXT_CHARS),
      html: text ? null : clip(mail.html ?? "", MAX_TEXT_CHARS) || null, // html only when there is no plain-text part
      messageId: mail.messageId ?? message.headers.get("Message-ID") ?? null,
      inReplyTo: mail.inReplyTo ?? null,
      receivedAt: new Date().toISOString(),
    };

    const body = JSON.stringify(payload);
    const bytes = new TextEncoder().encode(body);
    const signature = await hmacSha256Hex(env.RELAYHUB_INBOUND_SECRET, bytes);

    const response = await fetch(`${env.RELAYHUB_API_URL.replace(/\/$/, "")}/api/v1/hooks/email`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-RelayHub-Signature": `sha256=${signature}` },
      body,
    });

    // Optional copy to a real mailbox (FORWARD_TO must be a verified Email Routing destination).
    if (env.FORWARD_TO) ctx.waitUntil(message.forward(env.FORWARD_TO).catch((e) => console.error("forward failed", e)));

    if (!response.ok) {
      const detail = await response.text().catch(() => "");
      console.error(`RelayHub hook answered ${response.status}: ${detail}`);
      // Throwing makes Cloudflare answer the sending server with a temporary failure, so the mail is retried instead of lost.
      if (!env.FORWARD_TO) throw new Error(`RelayHub hook answered ${response.status}`);
    }
  },
};

async function hmacSha256Hex(secret, bytes) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, bytes);
  return [...new Uint8Array(mac)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function clip(s, max) {
  return s.length <= max ? s : s.slice(0, max) + "\n[truncated]";
}
