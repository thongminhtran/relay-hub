import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useMemo, useRef, useState, type FormEvent } from 'react'
import { ChannelGlyph, EnabledPill } from '../../components/Chips'
import { IconArrowRight, IconPlus, IconTrash } from '../../components/Icons'
import { ConfirmDialog, EmptyState, ErrorState, Modal, Notice, SkeletonRows } from '../../components/Primitives'
import { useDestinations, useRoutes, useSources } from '../../hooks/useData'
import { isApiError } from '../../lib/api'
import { DESTINATION_TYPE_LABEL, sourceChannel } from '../../lib/labels'
import { keys, mutations } from '../../lib/queries'
import { DEFAULT_TEMPLATE, renderTemplate, SAMPLE_TEMPLATE_VALUES, TEMPLATE_PLACEHOLDERS, unknownPlaceholders } from '../../lib/template'
import { useToast } from '../../lib/toast'
import type { Destination, Route, Source } from '../../lib/types'
import { EnabledSwitch, GatedButton, ReadOnlyNotice } from './shared'

export function RoutesTab() {
  const routes = useRoutes()
  const sources = useSources()
  const destinations = useDestinations()
  const [editing, setEditing] = useState<Route | 'new' | null>(null)

  const sourceById = useMemo(() => new Map((sources.data ?? []).map((s) => [s.id, s])), [sources.data])
  const destById = useMemo(() => new Map((destinations.data ?? []).map((d) => [d.id, d])), [destinations.data])
  const pending = routes.isPending || sources.isPending || destinations.isPending
  const error = routes.error ?? sources.error ?? destinations.error
  const canCreate = (sources.data?.length ?? 0) > 0 && (destinations.data?.length ?? 0) > 0

  return (
    <div className="stack">
      <div className="page-head">
        <p className="desc" style={{ marginTop: 0 }}>
          A route sends one source to one destination with a message template. Adding a channel is configuration, not new code; fixing a template
          fixes its replays too.
        </p>
        <GatedButton className="btn btn-primary" onClick={() => setEditing('new')} disabled={!canCreate}>
          <IconPlus /> New route
        </GatedButton>
      </div>
      <ReadOnlyNotice />
      {pending ? (
        <div className="panel">
          <SkeletonRows rows={3} />
        </div>
      ) : error ? (
        <ErrorState
          error={error}
          onRetry={() => {
            void routes.refetch()
            void sources.refetch()
            void destinations.refetch()
          }}
        />
      ) : routes.data && routes.data.length === 0 ? (
        <EmptyState title="No routes yet" action={canCreate ? <GatedButton className="btn btn-sm" onClick={() => setEditing('new')}>Create the first route</GatedButton> : undefined}>
          {canCreate ? 'Connect a source to a destination to start forwarding messages.' : 'Create at least one source and one destination first.'}
        </EmptyState>
      ) : routes.data ? (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th scope="col">Source</th>
                <th scope="col" aria-label="to" />
                <th scope="col">Destination</th>
                <th scope="col">Template</th>
                <th scope="col">Status</th>
                <th scope="col">
                  <span className="sr">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {routes.data.map((r) => (
                <RouteRow key={r.id} route={r} source={sourceById.get(r.sourceId)} destination={destById.get(r.destinationId)} onEdit={() => setEditing(r)} />
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      {editing ? <RouteModal route={editing === 'new' ? null : editing} sources={sources.data ?? []} destinations={destinations.data ?? []} onClose={() => setEditing(null)} /> : null}
    </div>
  )
}

function RouteRow({ route: r, source, destination, onEdit }: { route: Route; source?: Source; destination?: Destination; onEdit: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [confirm, setConfirm] = useState(false)
  const invalidate = () => queryClient.invalidateQueries({ queryKey: keys.routes })

  const toggle = useMutation({
    mutationFn: (isEnabled: boolean) => mutations.updateRoute(r.id, { isEnabled }),
    onSuccess: () => void invalidate(),
    onError: (e) => toast.error(e),
  })
  const remove = useMutation({
    mutationFn: () => mutations.deleteRoute(r.id),
    onSuccess: () => {
      toast.push('ok', 'Route deleted')
      setConfirm(false)
      void invalidate()
    },
    onError: (e) => toast.error(e),
  })

  return (
    <tr>
      <td>
        <span className="row-flex" style={{ flexWrap: 'nowrap' }}>
          {source ? <ChannelGlyph channel={sourceChannel(source.type)} size="sm" decorative /> : null}
          <span>{source?.name ?? <span className="muted">source #{r.sourceId}</span>}</span>
        </span>
      </td>
      <td aria-hidden="true">
        <IconArrowRight className="faint" />
      </td>
      <td>
        <b>{destination?.name ?? <span className="muted">destination #{r.destinationId}</span>}</b>
        {destination ? <div className="muted" style={{ fontSize: 12 }}>{DESTINATION_TYPE_LABEL[destination.type]}</div> : null}
      </td>
      <td>
        <pre className="tpl" style={{ margin: 0 }}>
          {r.template}
        </pre>
        {r.filter ? (
          <div className="muted" style={{ fontSize: 12 }}>
            filter: <code>{r.filter}</code>
          </div>
        ) : null}
      </td>
      <td>
        <EnabledPill enabled={r.isEnabled} />
      </td>
      <td>
        <span className="row-flex" style={{ flexWrap: 'nowrap', justifyContent: 'flex-end' }}>
          <EnabledSwitch checked={r.isEnabled} onChange={(v) => toggle.mutate(v)} busy={toggle.isPending} label={`Route ${r.id} enabled`} />
          <GatedButton onClick={onEdit}>Edit</GatedButton>
          <GatedButton className="btn btn-sm btn-danger btn-icon" onClick={() => setConfirm(true)} ariaLabel={`Delete route ${r.id}`}>
            <IconTrash />
          </GatedButton>
        </span>
        {confirm ? (
          <ConfirmDialog title="Delete this route?" message="New messages from the source stop going to this destination. Existing deliveries are kept." busy={remove.isPending} onConfirm={() => remove.mutate()} onCancel={() => setConfirm(false)} />
        ) : null}
      </td>
    </tr>
  )
}

function RouteModal({ route, sources, destinations, onClose }: { route: Route | null; sources: Source[]; destinations: Destination[]; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [sourceId, setSourceId] = useState<number>(route?.sourceId ?? sources[0]?.id ?? 0)
  const [destinationId, setDestinationId] = useState<number>(route?.destinationId ?? destinations[0]?.id ?? 0)
  const [template, setTemplate] = useState(route?.template ?? DEFAULT_TEMPLATE)
  const [filter, setFilter] = useState(route?.filter ?? '')
  const [error, setError] = useState<string | null>(null)
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  const source = sources.find((s) => s.id === sourceId)
  const sampleValues = useMemo(
    () => ({ ...SAMPLE_TEMPLATE_VALUES, channel: source ? sourceChannel(source.type) : SAMPLE_TEMPLATE_VALUES.channel, source: source?.name ?? SAMPLE_TEMPLATE_VALUES.source }),
    [source],
  )
  const preview = renderTemplate(template, sampleValues)
  const unknown = unknownPlaceholders(template)

  const save = useMutation({
    mutationFn: () =>
      route
        ? mutations.updateRoute(route.id, { template, filter: filter.trim() || undefined })
        : mutations.createRoute({ sourceId, destinationId, template, filter: filter.trim() || undefined }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: keys.routes })
      toast.push('ok', route ? 'Route updated' : 'Route created')
      onClose()
    },
    onError: (e) => {
      if (isApiError(e) && e.status === 400) setError(e.firstError ?? e.title)
      else toast.error(e)
    },
  })

  const insert = (token: string) => {
    const ta = textareaRef.current
    const text = `{{${token}}}`
    if (!ta) {
      setTemplate((t) => t + text)
      return
    }
    const start = ta.selectionStart ?? template.length
    const end = ta.selectionEnd ?? template.length
    const next = template.slice(0, start) + text + template.slice(end)
    setTemplate(next)
    requestAnimationFrame(() => {
      ta.focus()
      ta.setSelectionRange(start + text.length, start + text.length)
    })
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (!route && (!sourceId || !destinationId)) {
      setError('Pick a source and a destination')
      return
    }
    if (!template.trim()) {
      setError('Template cannot be empty')
      return
    }
    save.mutate()
  }

  return (
    <Modal title={route ? `Edit route #${route.id}` : 'New route'} onClose={onClose} description="The template is rendered for every delivery attempt, so editing it also changes what a replay sends.">
      <form onSubmit={submit} className="stack-sm" noValidate>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="rt-source">Source</label>
            <select id="rt-source" className="select" value={sourceId} onChange={(e) => setSourceId(Number(e.target.value))} disabled={route !== null}>
              {sources.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name} ({s.type})
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor="rt-dest">Destination</label>
            <select id="rt-dest" className="select" value={destinationId} onChange={(e) => setDestinationId(Number(e.target.value))} disabled={route !== null}>
              {destinations.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name} ({d.type})
                </option>
              ))}
            </select>
          </div>
          <div className="field span-2">
            <label htmlFor="rt-template">Template</label>
            <textarea id="rt-template" ref={textareaRef} className="textarea mono" rows={4} value={template} onChange={(e) => setTemplate(e.target.value)} spellCheck={false} required />
            <div className="placeholder-legend" aria-label="Insert placeholder">
              {TEMPLATE_PLACEHOLDERS.map((p) => (
                <button type="button" key={p} onClick={() => insert(p)} title={`Insert {{${p}}}`}>
                  {`{{${p}}}`}
                </button>
              ))}
            </div>
            {unknown.length ? (
              <span className="field-hint warn-c">
                Unknown placeholder{unknown.length > 1 ? 's' : ''} {unknown.map((u) => `{{${u}}}`).join(', ')} will render as empty text.
              </span>
            ) : null}
          </div>
          <div className="field span-2">
            <span className="field-label">Preview with sample values</span>
            <div className="preview" aria-live="polite">
              {preview || <span className="muted">(empty)</span>}
            </div>
          </div>
          <div className="field span-2">
            <label htmlFor="rt-filter">Filter (optional)</label>
            <input id="rt-filter" className="input mono" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="reserved for future use" autoComplete="off" />
          </div>
        </div>
        {route === null && destinations.length === 0 ? <Notice kind="warn">Create a destination before adding a route.</Notice> : null}
        {error ? (
          <p className="field-err" role="alert">
            {error}
          </p>
        ) : null}
        <div className="form-actions">
          <button type="button" className="btn" onClick={onClose} disabled={save.isPending}>
            Cancel
          </button>
          <GatedButton type="submit" className="btn btn-primary" disabled={save.isPending}>
            {save.isPending ? <span className="spinner" /> : null}
            {route ? 'Save changes' : 'Create route'}
          </GatedButton>
        </div>
      </form>
    </Modal>
  )
}
