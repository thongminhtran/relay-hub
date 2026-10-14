import { API_BASE } from './api'
import type { DestinationType, SourceType } from './types'

/** Webhook URL the platform (or your site) must call for a source of this type, and what authenticates it. */
export function webhookUrl(type: SourceType, name: string): { url: string; auth: string } {
  switch (type) {
    case 'telegram':
      return { url: `${API_BASE}/api/v1/hooks/telegram/${encodeURIComponent(name)}`, auth: 'X-Telegram-Bot-Api-Secret-Token' }
    case 'instagram':
    case 'facebook':
      return { url: `${API_BASE}/api/v1/hooks/meta`, auth: 'X-Hub-Signature-256 (Meta app secret)' }
    case 'email':
      return { url: `${API_BASE}/api/v1/hooks/email`, auth: 'X-RelayHub-Signature (inbound secret)' }
    default:
      return { url: `${API_BASE}/api/v1/hooks/${encodeURIComponent(name)}`, auth: 'X-Api-Key' }
  }
}

export const SOURCE_CONFIG_HINT: Record<SourceType, { placeholder: string; help: string }> = {
  generic: { placeholder: '{}', help: 'No config needed. Your site posts JSON with the API key in X-Api-Key.' },
  instagram: { placeholder: '{"pageAccessToken":"EAAB..."}', help: 'Page access token with instagram_manage_messages; used for replies through the Send API.' },
  facebook: { placeholder: '{"pageAccessToken":"EAAB..."}', help: 'Page access token with pages_messaging; used for replies through the Send API.' },
  telegram: { placeholder: '{"botToken":"123456:ABC-DEF..."}', help: 'Bot token from @BotFather; used to send replies.' },
  email: { placeholder: '{"fromAddress":"hello@yourdomain.com"}', help: 'Sender used for email replies (defaults to the workspace sender when empty).' },
}

export const SOURCE_EXTERNAL_ID_LABEL: Record<SourceType, string> = {
  generic: 'External account id (optional)',
  instagram: 'Instagram account id (entry.id in webhooks)',
  facebook: 'Facebook Page id (entry.id in webhooks)',
  telegram: 'Bot username (optional)',
  email: 'Inbound address (the "to" the worker matches)',
}

/** Parse a JSON object from a textarea; empty → undefined; errors → message. */
export function parseJsonObject(text: string): { value?: Record<string, unknown>; error?: string } {
  const t = text.trim()
  if (!t) return { value: undefined }
  try {
    const v: unknown = JSON.parse(t)
    if (typeof v !== 'object' || v === null || Array.isArray(v)) return { error: 'Must be a JSON object, e.g. {"key": "value"}' }
    return { value: v as Record<string, unknown> }
  } catch (e) {
    return { error: e instanceof Error ? `Invalid JSON: ${e.message}` : 'Invalid JSON' }
  }
}

/** Masked config → "key: value" rows for display. */
export function configRows(config: Record<string, unknown> | null | undefined): [string, string][] {
  if (!config) return []
  return Object.entries(config).map(([k, v]) => [k, typeof v === 'string' ? v : JSON.stringify(v)])
}

export interface DestinationForm {
  name: string
  type: DestinationType
  webhookUrl: string
  username: string
  to: string
  from: string
  subject: string
  botToken: string
  chatId: string
  url: string
  method: 'POST' | 'PUT'
  headers: string
  bodyTemplate: string
}

export const EMPTY_DESTINATION_FORM: DestinationForm = {
  name: '',
  type: 'discord',
  webhookUrl: '',
  username: '',
  to: '',
  from: '',
  subject: '',
  botToken: '',
  chatId: '',
  url: '',
  method: 'POST',
  headers: '',
  bodyTemplate: '',
}

/** Builds the per-type config object the API validates (DestinationEndpoints.ValidateConfig). */
export function buildDestinationConfig(f: DestinationForm): { config?: Record<string, unknown>; error?: string } {
  const req = (v: string, label: string) => (v.trim() ? null : `${label} is required`)
  switch (f.type) {
    case 'discord': {
      const e = req(f.webhookUrl, 'Webhook URL')
      if (e) return { error: e }
      return { config: { webhookUrl: f.webhookUrl.trim(), ...(f.username.trim() ? { username: f.username.trim() } : {}) } }
    }
    case 'teams': {
      const e = req(f.webhookUrl, 'Webhook URL')
      if (e) return { error: e }
      return { config: { webhookUrl: f.webhookUrl.trim() } }
    }
    case 'email': {
      const e = req(f.to, 'Recipient (to)')
      if (e) return { error: e }
      return { config: { to: f.to.trim(), ...(f.from.trim() ? { from: f.from.trim() } : {}), ...(f.subject.trim() ? { subject: f.subject.trim() } : {}) } }
    }
    case 'telegram': {
      const e = req(f.botToken, 'Bot token') ?? req(f.chatId, 'Chat id')
      if (e) return { error: e }
      return { config: { botToken: f.botToken.trim(), chatId: f.chatId.trim() } }
    }
    case 'http': {
      const e = req(f.url, 'URL')
      if (e) return { error: e }
      if (!/^https:\/\//i.test(f.url.trim())) return { error: 'URL must start with https://' }
      const headers = parseJsonObject(f.headers)
      if (headers.error) return { error: `Headers: ${headers.error}` }
      return {
        config: {
          url: f.url.trim(),
          method: f.method,
          ...(headers.value ? { headers: headers.value } : {}),
          ...(f.bodyTemplate.trim() ? { bodyTemplate: f.bodyTemplate } : {}),
        },
      }
    }
  }
}
