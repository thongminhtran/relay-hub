import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { ChannelGlyph, EnabledPill } from '../../components/Chips'
import { IconKey, IconPlus, IconTrash } from '../../components/Icons'
import { ConfirmDialog, CopyButton, EmptyState, ErrorState, Modal, Notice, SkeletonRows, TimeAgo } from '../../components/Primitives'
import { useSources } from '../../hooks/useData'
import { isApiError } from '../../lib/api'
import { configRows, parseJsonObject, SOURCE_CONFIG_HINT, SOURCE_EXTERNAL_ID_LABEL, webhookUrl } from '../../lib/config'
import { SOURCE_TYPE_LABEL, sourceChannel } from '../../lib/labels'
import { keys, mutations } from '../../lib/queries'
import { useToast } from '../../lib/toast'
import { SOURCE_TYPES, type CreateSourceRequest, type Source, type SourceType } from '../../lib/types'
import { EnabledSwitch, GatedButton, ReadOnlyNotice } from './shared'

export function SourcesTab() {
  const query = useSources()
  const [creating, setCreating] = useState(false)
  const [revealed, setRevealed] = useState<{ source: Source; apiKey: string; reason: 'created' | 'rotated' } | null>(null)

  return (
    <div className="stack">
      <div className="page-head">
        <p className="desc" style={{ marginTop: 0 }}>
          Where messages come from. Each source has its own webhook URL; generic sources authenticate with a signed API key that is shown exactly
          once.
        </p>
        <GatedButton className="btn btn-primary" onClick={() => setCreating(true)}>
          <IconPlus /> New source
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
        <EmptyState title="No sources yet" action={<GatedButton className="btn btn-sm" onClick={() => setCreating(true)}>Create the first source</GatedButton>}>
          A source is a channel account or a website form that sends messages into this workspace.
        </EmptyState>
      ) : query.data ? (
        <div className="cards">
          {query.data.map((s) => (
            <SourceCard key={s.id} source={s} onRevealed={(apiKey) => setRevealed({ source: s, apiKey, reason: 'rotated' })} />
          ))}
        </div>
      ) : null}

      {creating ? (
        <CreateSourceModal
          onClose={() => setCreating(false)}
          onCreated={(source, apiKey) => {
            setCreating(false)
            setRevealed({ source, apiKey, reason: 'created' })
          }}
        />
      ) : null}
      {revealed ? <ApiKeyModal source={revealed.source} apiKey={revealed.apiKey} reason={revealed.reason} onClose={() => setRevealed(null)} /> : null}
    </div>
  )
}

function SourceCard({ source: s, onRevealed }: { source: Source; onRevealed: (apiKey: string) => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [confirm, setConfirm] = useState<'delete' | 'rotate' | null>(null)
  const invalidate = () => queryClient.invalidateQueries({ queryKey: keys.sources })

  const toggle = useMutation({
    mutationFn: (isEnabled: boolean) => mutations.updateSource(s.id, { isEnabled }),
    onSuccess: (_r, isEnabled) => {
      toast.push('ok', isEnabled ? 'Source enabled' : 'Source disabled', s.name)
      void invalidate()
    },
    onError: (e) => toast.error(e),
  })
  const rotate = useMutation({
    mutationFn: () => mutations.rotateSourceKey(s.id),
    onSuccess: (r) => {
      setConfirm(null)
      void invalidate()
      onRevealed(r.apiKey)
    },
    onError: (e) => toast.error(e),
  })
  const remove = useMutation({
    mutationFn: () => mutations.deleteSource(s.id),
    onSuccess: () => {
      toast.push('ok', 'Source deleted', s.name)
      setConfirm(null)
      void invalidate()
      void queryClient.invalidateQueries({ queryKey: keys.routes })
    },
    onError: (e) => toast.error(e),
  })

  const hook = webhookUrl(s.type, s.name)
  const rows = configRows(s.config)

  return (
    <article className={`card${s.isEnabled ? '' : ' is-disabled'}`} aria-label={s.name}>
      <div className="card-head">
        <ChannelGlyph channel={sourceChannel(s.type)} />
        <div>
          <h3>{s.name}</h3>
          <p>
            {SOURCE_TYPE_LABEL[s.type]} · created <TimeAgo value={s.createdAt} />
          </p>
        </div>
        <EnabledPill enabled={s.isEnabled} />
      </div>
      <dl className="kvs">
        <dt>Key id</dt>
        <dd>
          <code>{s.keyId}</code>
        </dd>
        {s.externalAccountId ? (
          <>
            <dt>Account</dt>
            <dd>
              <code>{s.externalAccountId}</code>
            </dd>
          </>
        ) : null}
        <dt>Verified by</dt>
        <dd>{hook.auth}</dd>
        {rows.map(([k, v]) => (
          <SourceConfigRow key={k} k={k} v={v} />
        ))}
      </dl>
      <div>
        <span className="field-label">Webhook URL</span>
        <div className="copybox" style={{ marginTop: 4 }}>
          <code>
            POST {hook.url}
          </code>
          <CopyButton text={hook.url} label="Copy" quiet={false} />
        </div>
      </div>
      <div className="card-foot">
        <EnabledSwitch checked={s.isEnabled} onChange={(v) => toggle.mutate(v)} busy={toggle.isPending} label={`${s.name} enabled`} />
        <span style={{ flex: 1 }} />
        <GatedButton onClick={() => setConfirm('rotate')} disabled={rotate.isPending}>
          <IconKey /> Rotate key
        </GatedButton>
        <GatedButton className="btn btn-sm btn-danger" onClick={() => setConfirm('delete')} ariaLabel={`Delete ${s.name}`}>
          <IconTrash /> Delete
        </GatedButton>
      </div>
      {confirm === 'delete' ? (
        <ConfirmDialog
          title={`Delete source "${s.name}"?`}
          message="Its routes are deleted too and its webhook URL stops working immediately. Stored messages are kept."
          busy={remove.isPending}
          onConfirm={() => remove.mutate()}
          onCancel={() => setConfirm(null)}
        />
      ) : null}
      {confirm === 'rotate' ? (
        <ConfirmDialog
          title="Rotate the API key?"
          message="A new key is issued and shown once. The current key stops working immediately — update the site that posts to this source right after."
          confirmLabel="Rotate key"
          danger={false}
          busy={rotate.isPending}
          onConfirm={() => rotate.mutate()}
          onCancel={() => setConfirm(null)}
        />
      ) : null}
    </article>
  )
}

function SourceConfigRow({ k, v }: { k: string; v: string }) {
  return (
    <>
      <dt>{k}</dt>
      <dd>
        <code>{v}</code>
      </dd>
    </>
  )
}

function CreateSourceModal({ onClose, onCreated }: { onClose: () => void; onCreated: (source: Source, apiKey: string) => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [type, setType] = useState<SourceType>('generic')
  const [externalAccountId, setExternalAccountId] = useState('')
  const [configText, setConfigText] = useState('')
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: (body: CreateSourceRequest) => mutations.createSource(body),
    onSuccess: (res) => {
      void queryClient.invalidateQueries({ queryKey: keys.sources })
      toast.push('ok', 'Source created', res.source.name)
      onCreated(res.source, res.apiKey)
    },
    onError: (e) => {
      if (isApiError(e) && e.status === 400) setError(e.firstError ?? e.title)
      else toast.error(e)
    },
  })

  const submit = (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    const n = name.trim()
    if (!/^[a-z0-9][a-z0-9-_]{1,63}$/i.test(n)) {
      setError('Name: 2–64 letters, digits, dashes or underscores (it becomes part of the webhook URL).')
      return
    }
    const cfg = parseJsonObject(configText)
    if (cfg.error) {
      setError(cfg.error)
      return
    }
    const body: CreateSourceRequest = { name: n, type }
    if (externalAccountId.trim()) body.externalAccountId = externalAccountId.trim()
    if (cfg.value) body.config = cfg.value
    create.mutate(body)
  }

  const hint = SOURCE_CONFIG_HINT[type]
  const preview = webhookUrl(type, name.trim() || '{name}')

  return (
    <Modal title="New source" onClose={onClose} description="Create the source first, then point the platform's webhook at the URL shown below.">
      <form onSubmit={submit} className="stack-sm" noValidate>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="src-name">Name</label>
            <input id="src-name" className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="website-contact" autoComplete="off" required />
            <span className="field-hint">Unique in the workspace; used in the webhook URL.</span>
          </div>
          <div className="field">
            <label htmlFor="src-type">Type</label>
            <select id="src-type" className="select" value={type} onChange={(e) => setType(e.target.value as SourceType)}>
              {SOURCE_TYPES.map((t) => (
                <option key={t} value={t}>
                  {SOURCE_TYPE_LABEL[t]}
                </option>
              ))}
            </select>
          </div>
          <div className="field span-2">
            <label htmlFor="src-ext">{SOURCE_EXTERNAL_ID_LABEL[type]}</label>
            <input id="src-ext" className="input" value={externalAccountId} onChange={(e) => setExternalAccountId(e.target.value)} autoComplete="off" />
          </div>
          <div className="field span-2">
            <label htmlFor="src-config">Config (JSON)</label>
            <textarea id="src-config" className="textarea mono" rows={3} value={configText} onChange={(e) => setConfigText(e.target.value)} placeholder={hint.placeholder} spellCheck={false} />
            <span className="field-hint">{hint.help} Secrets are stored encrypted-at-rest and returned masked.</span>
          </div>
        </div>
        <div className="field">
          <span className="field-label">Webhook URL for this type</span>
          <div className="copybox">
            <code>POST {preview.url}</code>
            <CopyButton text={preview.url} quiet={false} />
          </div>
          <span className="field-hint">Authenticated with {preview.auth}.</span>
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
          <GatedButton type="submit" className="btn btn-primary" disabled={create.isPending || !name.trim()}>
            {create.isPending ? <span className="spinner" /> : null}
            Create source
          </GatedButton>
        </div>
      </form>
    </Modal>
  )
}

function ApiKeyModal({ source, apiKey, reason, onClose }: { source: Source; apiKey: string; reason: 'created' | 'rotated'; onClose: () => void }) {
  const hook = webhookUrl(source.type, source.name)
  return (
    <Modal title={reason === 'created' ? 'Source created — copy the API key now' : 'New API key — copy it now'} onClose={onClose}>
      <Notice kind="warn">
        <b>Store this key now.</b> It is derived, not stored: RelayHub cannot show it again. If you lose it, rotate the key.
      </Notice>
      <div className="field">
        <span className="field-label">API key for {source.name}</span>
        <div className="copybox secret">
          <code>{apiKey}</code>
          <CopyButton text={apiKey} label="Copy key" quiet={false} />
        </div>
      </div>
      <div className="field">
        <span className="field-label">Send messages to</span>
        <div className="copybox">
          <code>POST {hook.url}</code>
          <CopyButton text={hook.url} quiet={false} />
        </div>
        <span className="field-hint">
          {source.type === 'generic' ? (
            <>
              Header <code>X-Api-Key: &lt;key&gt;</code>, JSON body with <code>name</code>, <code>email</code>, <code>message</code> (≤ 32 KB).
            </>
          ) : (
            <>Platform webhooks are verified by {hook.auth}; the API key is only needed for manual tests.</>
          )}
        </span>
      </div>
      <div className="form-actions">
        <button type="button" className="btn btn-primary" onClick={onClose}>
          I stored it
        </button>
      </div>
    </Modal>
  )
}
