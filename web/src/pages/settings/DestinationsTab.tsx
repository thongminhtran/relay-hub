import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { EnabledPill } from '../../components/Chips'
import { IconPlus, IconSend, IconTrash } from '../../components/Icons'
import { ConfirmDialog, EmptyState, ErrorState, Modal, SkeletonRows } from '../../components/Primitives'
import { useDestinations } from '../../hooks/useData'
import { isApiError } from '../../lib/api'
import { buildDestinationConfig, configRows, EMPTY_DESTINATION_FORM, type DestinationForm } from '../../lib/config'
import { DESTINATION_TYPE_LABEL } from '../../lib/labels'
import { useAuth } from '../../lib/auth'
import { keys, mutations } from '../../lib/queries'
import { useToast } from '../../lib/toast'
import { DESTINATION_TYPES, type CreateDestinationRequest, type Destination, type DestinationTestResult, type DestinationType } from '../../lib/types'
import { EnabledSwitch, GatedButton, ReadOnlyNotice } from './shared'

const TYPE_GLYPH: Record<DestinationType, string> = { discord: 'DC', teams: 'TM', email: '@', telegram: 'TG', http: 'API' }
const TYPE_HELP: Record<DestinationType, string> = {
  discord: 'Channel webhook from Discord → Server settings → Integrations → Webhooks.',
  teams: 'Power Automate "When a Teams webhook request is received" or a Workflows webhook (host *.logic.azure.com / *.webhook.office.com).',
  email: 'Sent through Resend from the workspace sender unless you set a verified "from".',
  telegram: 'Bot token from @BotFather and the chat id of the group or user the bot should post to.',
  http: 'Any HTTPS endpoint. Private, loopback and link-local addresses are refused (SSRF guard). Not available in demo workspaces.',
}

export function DestinationsTab() {
  const query = useDestinations()
  const [creating, setCreating] = useState(false)

  return (
    <div className="stack">
      <div className="page-head">
        <p className="desc" style={{ marginTop: 0 }}>
          Where notifications go. Configs are stored encrypted and come back masked; "Send test" posts a sample message through the real connector
          (mocked in the demo workspace).
        </p>
        <GatedButton className="btn btn-primary" onClick={() => setCreating(true)}>
          <IconPlus /> New destination
        </GatedButton>
      </div>
      <ReadOnlyNotice />
      {query.isPending ? (
        <div className="panel">
          <SkeletonRows rows={4} />
        </div>
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} />
      ) : query.data && query.data.length === 0 ? (
        <EmptyState title="No destinations yet" action={<GatedButton className="btn btn-sm" onClick={() => setCreating(true)}>Create the first destination</GatedButton>}>
          Add Discord, Teams, email, Telegram or an HTTPS endpoint, then connect it to a source with a route.
        </EmptyState>
      ) : query.data ? (
        <div className="cards">
          {query.data.map((d) => (
            <DestinationCard key={d.id} destination={d} />
          ))}
        </div>
      ) : null}
      {creating ? <CreateDestinationModal onClose={() => setCreating(false)} /> : null}
    </div>
  )
}

function DestinationCard({ destination: d }: { destination: Destination }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [confirm, setConfirm] = useState(false)
  const [testResult, setTestResult] = useState<DestinationTestResult | null>(null)
  const invalidate = () => queryClient.invalidateQueries({ queryKey: keys.destinations })

  const toggle = useMutation({
    mutationFn: (isEnabled: boolean) => mutations.updateDestination(d.id, { isEnabled }),
    onSuccess: (_r, isEnabled) => {
      toast.push('ok', isEnabled ? 'Destination enabled' : 'Destination disabled', d.name)
      void invalidate()
    },
    onError: (e) => toast.error(e),
  })
  const test = useMutation({
    mutationFn: () => mutations.testDestination(d.id),
    onSuccess: (r) => {
      setTestResult(r)
      if (r.succeeded) toast.push('ok', 'Test delivered', `${d.name}${r.httpStatus ? ` · HTTP ${r.httpStatus}` : ''}`)
      else toast.push('bad', 'Test failed', r.error ?? (r.httpStatus ? `HTTP ${r.httpStatus}` : undefined))
    },
    onError: (e) => toast.error(e, 'Test failed'),
  })
  const remove = useMutation({
    mutationFn: () => mutations.deleteDestination(d.id),
    onSuccess: () => {
      toast.push('ok', 'Destination deleted', d.name)
      setConfirm(false)
      void invalidate()
      void queryClient.invalidateQueries({ queryKey: keys.routes })
    },
    onError: (e) => toast.error(e),
  })

  const rows = configRows(d.config)
  return (
    <article className={`card${d.isEnabled ? '' : ' is-disabled'}`} aria-label={d.name}>
      <div className="card-head">
        <span className="glyph g-out" aria-hidden="true">
          {TYPE_GLYPH[d.type] ?? d.type.slice(0, 3).toUpperCase()}
        </span>
        <div>
          <h3>{d.name}</h3>
          <p>{DESTINATION_TYPE_LABEL[d.type] ?? d.type}</p>
        </div>
        <EnabledPill enabled={d.isEnabled} />
      </div>
      {rows.length ? (
        <dl className="kvs">
          {rows.map(([k, v]) => (
            <ConfigRow key={k} k={k} v={v} />
          ))}
        </dl>
      ) : (
        <p className="policy">No configuration.</p>
      )}
      {testResult ? (
        <div className={`result-box ${testResult.succeeded ? 'ok' : 'bad'}`} role="status">
          {testResult.succeeded ? 'succeeded' : 'failed'}
          {testResult.httpStatus !== null ? ` · HTTP ${testResult.httpStatus}` : ''}
          {testResult.error ? ` · ${testResult.error}` : ''}
        </div>
      ) : null}
      <div className="card-foot">
        <EnabledSwitch checked={d.isEnabled} onChange={(v) => toggle.mutate(v)} busy={toggle.isPending} label={`${d.name} enabled`} />
        <span style={{ flex: 1 }} />
        <button type="button" className="btn btn-sm" onClick={() => test.mutate()} disabled={test.isPending || !d.isEnabled} title={d.isEnabled ? 'Post a sample message' : 'Enable the destination first'}>
          {test.isPending ? <span className="spinner" /> : <IconSend />}
          Send test
        </button>
        <GatedButton className="btn btn-sm btn-danger" onClick={() => setConfirm(true)} ariaLabel={`Delete ${d.name}`}>
          <IconTrash /> Delete
        </GatedButton>
      </div>
      {confirm ? (
        <ConfirmDialog
          title={`Delete destination "${d.name}"?`}
          message="Routes to this destination are deleted too. Past deliveries keep their history."
          busy={remove.isPending}
          onConfirm={() => remove.mutate()}
          onCancel={() => setConfirm(false)}
        />
      ) : null}
    </article>
  )
}

function ConfigRow({ k, v }: { k: string; v: string }) {
  return (
    <>
      <dt>{k}</dt>
      <dd>
        <code>{v}</code>
      </dd>
    </>
  )
}

function CreateDestinationModal({ onClose }: { onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const { isDemo } = useAuth()
  const [f, setF] = useState<DestinationForm>(EMPTY_DESTINATION_FORM)
  const [error, setError] = useState<string | null>(null)
  const set = <K extends keyof DestinationForm>(k: K, v: DestinationForm[K]) => setF((s) => ({ ...s, [k]: v }))

  const create = useMutation({
    mutationFn: (body: CreateDestinationRequest) => mutations.createDestination(body),
    onSuccess: (d) => {
      void queryClient.invalidateQueries({ queryKey: keys.destinations })
      toast.push('ok', 'Destination created', d.name)
      onClose()
    },
    onError: (e) => {
      if (isApiError(e) && (e.status === 400 || e.status === 403)) setError(e.firstError ?? e.title)
      else toast.error(e)
    },
  })

  const submit = (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (!f.name.trim()) {
      setError('Name is required')
      return
    }
    const built = buildDestinationConfig(f)
    if (built.error || !built.config) {
      setError(built.error ?? 'Invalid configuration')
      return
    }
    create.mutate({ name: f.name.trim(), type: f.type, config: built.config })
  }

  return (
    <Modal title="New destination" onClose={onClose} description="Secrets are stored encrypted and never returned in full.">
      <form onSubmit={submit} className="stack-sm" noValidate>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="dst-name">Name</label>
            <input id="dst-name" className="input" value={f.name} onChange={(e) => set('name', e.target.value)} placeholder="Discord #orders" autoComplete="off" required />
          </div>
          <div className="field">
            <label htmlFor="dst-type">Type</label>
            <select id="dst-type" className="select" value={f.type} onChange={(e) => set('type', e.target.value as DestinationType)}>
              {DESTINATION_TYPES.map((t) => (
                <option key={t} value={t} disabled={t === 'http' && isDemo}>
                  {DESTINATION_TYPE_LABEL[t]}
                  {t === 'http' && isDemo ? ' (not in demo)' : ''}
                </option>
              ))}
            </select>
          </div>
          <p className="field-hint span-2">{TYPE_HELP[f.type]}</p>

          {f.type === 'discord' || f.type === 'teams' ? (
            <div className="field span-2">
              <label htmlFor="dst-webhook">Webhook URL</label>
              <input id="dst-webhook" className="input mono" type="url" value={f.webhookUrl} onChange={(e) => set('webhookUrl', e.target.value)} placeholder={f.type === 'discord' ? 'https://discord.com/api/webhooks/…' : 'https://prod-00.canadacentral.logic.azure.com/…'} autoComplete="off" required />
            </div>
          ) : null}
          {f.type === 'discord' ? (
            <div className="field span-2">
              <label htmlFor="dst-username">Bot username (optional)</label>
              <input id="dst-username" className="input" value={f.username} onChange={(e) => set('username', e.target.value)} placeholder="RelayHub" autoComplete="off" />
            </div>
          ) : null}
          {f.type === 'email' ? (
            <>
              <div className="field span-2">
                <label htmlFor="dst-to">To</label>
                <input id="dst-to" className="input" type="email" value={f.to} onChange={(e) => set('to', e.target.value)} placeholder="owner@example.com" autoComplete="off" required />
              </div>
              <div className="field">
                <label htmlFor="dst-from">From (optional, verified sender)</label>
                <input id="dst-from" className="input" type="email" value={f.from} onChange={(e) => set('from', e.target.value)} autoComplete="off" />
              </div>
              <div className="field">
                <label htmlFor="dst-subject">Subject (optional)</label>
                <input id="dst-subject" className="input" value={f.subject} onChange={(e) => set('subject', e.target.value)} placeholder="New message from {{name}}" autoComplete="off" />
              </div>
            </>
          ) : null}
          {f.type === 'telegram' ? (
            <>
              <div className="field">
                <label htmlFor="dst-token">Bot token</label>
                <input id="dst-token" className="input mono" value={f.botToken} onChange={(e) => set('botToken', e.target.value)} placeholder="123456:ABC-DEF…" autoComplete="off" required />
              </div>
              <div className="field">
                <label htmlFor="dst-chat">Chat id</label>
                <input id="dst-chat" className="input mono" value={f.chatId} onChange={(e) => set('chatId', e.target.value)} placeholder="-1001234567890" autoComplete="off" required />
              </div>
            </>
          ) : null}
          {f.type === 'http' ? (
            <>
              <div className="field span-2">
                <label htmlFor="dst-url">URL (https only)</label>
                <input id="dst-url" className="input mono" type="url" value={f.url} onChange={(e) => set('url', e.target.value)} placeholder="https://api.example.com/relayhub" autoComplete="off" required />
              </div>
              <div className="field">
                <label htmlFor="dst-method">Method</label>
                <select id="dst-method" className="select" value={f.method} onChange={(e) => set('method', e.target.value as 'POST' | 'PUT')}>
                  <option value="POST">POST</option>
                  <option value="PUT">PUT</option>
                </select>
              </div>
              <div className="field">
                <label htmlFor="dst-headers">Headers (JSON object, optional)</label>
                <textarea id="dst-headers" className="textarea mono" rows={2} value={f.headers} onChange={(e) => set('headers', e.target.value)} placeholder='{"Authorization":"Bearer …"}' spellCheck={false} />
              </div>
              <div className="field span-2">
                <label htmlFor="dst-body">Body template (optional)</label>
                <textarea id="dst-body" className="textarea mono" rows={3} value={f.bodyTemplate} onChange={(e) => set('bodyTemplate', e.target.value)} placeholder='{"text":"{{text}}","from":"{{name}}"}' spellCheck={false} />
                <span className="field-hint">Same placeholders as route templates; defaults to the rendered route template as JSON.</span>
              </div>
            </>
          ) : null}
        </div>
        {error ? (
          <p className="field-err" role="alert">
            {error}
          </p>
        ) : null}
        <div className="form-actions">
          <button type="button" className="btn" onClick={onClose} disabled={create.isPending}>
            Cancel
          </button>
          <GatedButton type="submit" className="btn btn-primary" disabled={create.isPending}>
            {create.isPending ? <span className="spinner" /> : null}
            Create destination
          </GatedButton>
        </div>
      </form>
    </Modal>
  )
}
