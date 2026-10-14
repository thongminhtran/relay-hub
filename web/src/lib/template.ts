/** Mirrors RelayHub.Core.Templates.TemplateRenderer: `{{ name }}` tokens, unknown tokens render as empty text. */
const TOKEN = /\{\{\s*(\w+)\s*\}\}/g

export const TEMPLATE_PLACEHOLDERS = ['name', 'email', 'text', 'channel', 'source', 'receivedAt'] as const
export type TemplatePlaceholder = (typeof TEMPLATE_PLACEHOLDERS)[number]

export const DEFAULT_TEMPLATE = 'New message from {{name}} ({{channel}} · {{source}})\n{{text}}'

export const SAMPLE_TEMPLATE_VALUES: Record<TemplatePlaceholder, string> = {
  name: 'Linh Nguyen',
  email: 'linh@example.com',
  text: 'Do you have vegan options? Bringing 4 friends on Saturday!',
  channel: 'instagram',
  source: 'studio-instagram',
  receivedAt: '2026-10-14T16:42:07Z',
}

export function renderTemplate(template: string, values: Record<string, string | null | undefined>): string {
  return template.replace(TOKEN, (_m, key: string) => values[key] ?? '')
}

/** Tokens in the template that are not known placeholders (shown as a hint in the editor). */
export function unknownPlaceholders(template: string): string[] {
  const known = new Set<string>(TEMPLATE_PLACEHOLDERS)
  const out = new Set<string>()
  for (const m of template.matchAll(TOKEN)) {
    const key = m[1]
    if (key && !known.has(key)) out.add(key)
  }
  return [...out]
}
