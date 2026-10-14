import type { Channel, DeliveryStatus, DestinationType, SourceType } from './types'

export const CHANNEL_META: Record<Channel, { label: string; glyph: string }> = {
  web: { label: 'Website form', glyph: 'WEB' },
  instagram: { label: 'Instagram', glyph: 'IG' },
  facebook: { label: 'Facebook', glyph: 'FB' },
  telegram: { label: 'Telegram', glyph: 'TG' },
  email: { label: 'Email', glyph: '@' },
}

export function channelLabel(channel: string): string {
  return (CHANNEL_META as Record<string, { label: string }>)[channel]?.label ?? channel
}

export const SOURCE_TYPE_LABEL: Record<SourceType, string> = {
  generic: 'Website form / generic webhook',
  instagram: 'Instagram',
  facebook: 'Facebook Page',
  telegram: 'Telegram bot',
  email: 'Inbound email',
}

/** Channel a source type produces (generic sources store "web"). */
export function sourceChannel(type: SourceType): Channel {
  return type === 'generic' ? 'web' : type
}

export const DESTINATION_TYPE_LABEL: Record<DestinationType, string> = {
  discord: 'Discord',
  teams: 'Microsoft Teams',
  email: 'Email',
  telegram: 'Telegram',
  http: 'HTTP webhook',
}

export const STATUS_CLASS: Record<DeliveryStatus, string> = {
  Pending: 'pill-info',
  Delivered: 'pill-ok',
  Retrying: 'pill-warn',
  DeadLettered: 'pill-bad',
}

export const STATUS_LABEL: Record<DeliveryStatus, string> = {
  Pending: 'Pending',
  Delivered: 'Delivered',
  Retrying: 'Retrying',
  DeadLettered: 'Dead-lettered',
}
