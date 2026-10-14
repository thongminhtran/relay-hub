import { describe, expect, it } from 'vitest'
import { API_BASE } from './api'
import { buildDestinationConfig, EMPTY_DESTINATION_FORM, parseJsonObject, webhookUrl } from './config'
import { renderTemplate, SAMPLE_TEMPLATE_VALUES, unknownPlaceholders } from './template'

describe('webhookUrl', () => {
  it('follows the contract per source type', () => {
    expect(webhookUrl('generic', 'website-contact').url).toBe(`${API_BASE}/api/v1/hooks/website-contact`)
    expect(webhookUrl('telegram', 'prairie-bot').url).toBe(`${API_BASE}/api/v1/hooks/telegram/prairie-bot`)
    expect(webhookUrl('instagram', 'x').url).toBe(`${API_BASE}/api/v1/hooks/meta`)
    expect(webhookUrl('facebook', 'x').url).toBe(`${API_BASE}/api/v1/hooks/meta`)
    expect(webhookUrl('email', 'x').url).toBe(`${API_BASE}/api/v1/hooks/email`)
    expect(webhookUrl('generic', 'with space').url).toContain('with%20space')
  })
})

describe('buildDestinationConfig', () => {
  it('builds the per-type config objects the API validates', () => {
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'discord', webhookUrl: ' https://discord.com/api/webhooks/1 ', username: 'RelayHub' })).toEqual({
      config: { webhookUrl: 'https://discord.com/api/webhooks/1', username: 'RelayHub' },
    })
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'teams', webhookUrl: 'https://x.logic.azure.com/w' })).toEqual({ config: { webhookUrl: 'https://x.logic.azure.com/w' } })
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'email', to: 'a@b.c' })).toEqual({ config: { to: 'a@b.c' } })
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'telegram', botToken: 't', chatId: '-1' })).toEqual({ config: { botToken: 't', chatId: '-1' } })
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'http', url: 'https://api.example.com/h', method: 'PUT', headers: '{"X-Key":"1"}', bodyTemplate: '{"t":"{{text}}"}' })).toEqual({
      config: { url: 'https://api.example.com/h', method: 'PUT', headers: { 'X-Key': '1' }, bodyTemplate: '{"t":"{{text}}"}' },
    })
  })
  it('reports the first missing or invalid field', () => {
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'discord' }).error).toBe('Webhook URL is required')
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'telegram', botToken: 't' }).error).toBe('Chat id is required')
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'http', url: 'http://insecure' }).error).toMatch(/https/)
    expect(buildDestinationConfig({ ...EMPTY_DESTINATION_FORM, type: 'http', url: 'https://ok', headers: '[1]' }).error).toMatch(/Headers/)
  })
})

describe('parseJsonObject', () => {
  it('accepts objects, rejects everything else', () => {
    expect(parseJsonObject('')).toEqual({ value: undefined })
    expect(parseJsonObject('{"a":1}')).toEqual({ value: { a: 1 } })
    expect(parseJsonObject('[1]').error).toMatch(/object/)
    expect(parseJsonObject('{').error).toMatch(/Invalid JSON/)
  })
})

describe('renderTemplate', () => {
  it('mirrors the server renderer: tolerant spacing, unknown tokens → empty', () => {
    expect(renderTemplate('{{ name }} via {{channel}}: {{text}} {{nope}}!', SAMPLE_TEMPLATE_VALUES)).toBe('Linh Nguyen via instagram: Do you have vegan options? Bringing 4 friends on Saturday! !')
    expect(unknownPlaceholders('{{name}} {{nope}} {{ also_unknown }} {{email}}')).toEqual(['nope', 'also_unknown'])
  })
})
