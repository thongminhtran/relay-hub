import { useState, type FormEvent } from 'react'
import { useDemoFailureMode, useDemoReset, useSetFailureMode, useSimulate } from '../hooks/useDemo'
import { CHANNEL_META } from '../lib/labels'
import { DEMO_CHANNELS, DEMO_MAX_TEXT, type DemoChannel } from '../lib/types'
import { ChannelGlyph } from './Chips'
import { IconBolt, IconChevron, IconRefresh, IconSend } from './Icons'
import { ConfirmDialog } from './Primitives'

const FAILURE_HELP = 'When on, the Flaky endpoint answers 503: watch the website-form delivery retry at 2 s, 4 s, 8 s, 16 s, 32 s, dead-letter, then press Replay.'

function isNarrow(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(max-width: 899px)').matches
}

/**
 * Week 5 demo controls (plan section 9): simulate a provider webhook through the real pipeline, write your own message,
 * flip the failure switch, reset the sample data. Rendered only for Demo-workspace users.
 * `collapsible` wraps it in <details>, closed by default on phones unless `forceOpen`.
 */
export function DemoPanel({ collapsible = false, forceOpen = false }: { collapsible?: boolean; forceOpen?: boolean }) {
  // `forceOpen` (the ?demo=open landing flag) only decides the initial state; the user can still fold it afterwards.
  const [open, setOpen] = useState(() => forceOpen || !isNarrow())

  const body = <DemoControls />
  if (!collapsible) return <section className="demo-panel" aria-label="Demo controls">{body}</section>
  return (
    <details className="demo-panel" open={open} onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary>
        <IconChevron />
        <span className="eyebrow">Demo workspace</span>
        <span className="demo-title">Try the pipeline yourself</span>
        <span className="muted demo-sub">Simulate a message, break a destination, replay the dead letter.</span>
      </summary>
      {body}
    </details>
  )
}

function DemoControls() {
  const simulate = useSimulate()
  const failure = useDemoFailureMode()
  const setFailure = useSetFailureMode()
  const reset = useDemoReset()
  const [confirmReset, setConfirmReset] = useState(false)

  const [channel, setChannel] = useState<DemoChannel>('instagram')
  const [senderName, setSenderName] = useState('')
  const [text, setText] = useState('')
  const [lastChannel, setLastChannel] = useState<DemoChannel | null>(null)
  const over = text.length > DEMO_MAX_TEXT

  const quick = (c: DemoChannel) => {
    setLastChannel(c)
    simulate.mutate({ channel: c })
  }
  const sendOwn = (e: FormEvent) => {
    e.preventDefault()
    const t = text.trim()
    if (!t || over) return
    setLastChannel(channel)
    simulate.mutate({ channel, text: t, ...(senderName.trim() ? { senderName: senderName.trim() } : {}) }, { onSuccess: () => setText('') })
  }

  const failing = failure.data?.enabled ?? false

  return (
    <div className="demo-body">
      <div className="demo-col">
        <div>
          <p className="h-sm">Simulate a message</p>
          <p className="muted" style={{ fontSize: 13, marginBottom: 10 }}>
            Each button queues a correctly shaped provider webhook. It goes through the same queue, worker and routes as a real one and
            appears in the inbox a few seconds later.
          </p>
          <div className="sim-btns" role="group" aria-label="Simulate a message">
            {DEMO_CHANNELS.map((c) => (
              <button key={c} type="button" className="sim-btn" onClick={() => quick(c)} disabled={simulate.isPending}>
                {simulate.isPending && lastChannel === c ? <span className="spinner" /> : <ChannelGlyph channel={c} size="sm" decorative />}
                {CHANNEL_META[c].label}
              </button>
            ))}
          </div>
        </div>

        <form className="demo-own" onSubmit={sendOwn} aria-label="Send your own message">
          <p className="h-sm">Send your own</p>
          <div className="demo-own-row">
            <label className="sr" htmlFor="demo-channel">
              Channel
            </label>
            <select id="demo-channel" className="select" value={channel} onChange={(e) => setChannel(e.target.value as DemoChannel)}>
              {DEMO_CHANNELS.map((c) => (
                <option key={c} value={c}>
                  {CHANNEL_META[c].label}
                </option>
              ))}
            </select>
            <label className="sr" htmlFor="demo-sender">
              Sender name (optional)
            </label>
            <input id="demo-sender" className="input" placeholder="Sender name (optional)" value={senderName} onChange={(e) => setSenderName(e.target.value)} maxLength={100} autoComplete="off" />
          </div>
          <div className="demo-own-row">
            <label className="sr" htmlFor="demo-text">
              Message
            </label>
            <input id="demo-text" className="input" placeholder="Do you cater for 30 people next Friday?" value={text} onChange={(e) => setText(e.target.value)} maxLength={DEMO_MAX_TEXT + 50} aria-invalid={over || undefined} autoComplete="off" />
            <button type="submit" className="btn btn-primary" disabled={!text.trim() || over || simulate.isPending}>
              <IconSend /> Send
            </button>
          </div>
          <span className={`field-hint num${over ? ' bad-c' : ''}`}>
            {text.length} / {DEMO_MAX_TEXT}
          </span>
        </form>
      </div>

      <div className="demo-col">
        <label className={`switch-card${failing ? ' is-on' : ''}`} htmlFor="demo-failure">
          <input
            id="demo-failure"
            type="checkbox"
            checked={failing}
            disabled={failure.isPending || setFailure.isPending}
            onChange={(e) => setFailure.mutate(e.target.checked)}
            aria-describedby="demo-failure-help"
          />
          <span className="track" />
          <span>
            <b>
              <IconBolt /> Simulate failure {failure.isError ? <span className="pill pill-mute pill-plain">unavailable</span> : null}
            </b>
            <small id="demo-failure-help">{FAILURE_HELP}</small>
          </span>
        </label>
        <div className="demo-reset">
          <div>
            <b>Reset demo data</b>
            <p className="muted" style={{ fontSize: 12.5 }}>
              Deletes every event and conversation of the demo workspace, turns the failure switch off and re-seeds the samples.
            </p>
          </div>
          <button type="button" className="btn btn-sm" onClick={() => setConfirmReset(true)} disabled={reset.isPending}>
            {reset.isPending ? <span className="spinner" /> : <IconRefresh />}
            Reset
          </button>
        </div>
      </div>

      {confirmReset ? (
        <ConfirmDialog
          title="Reset the demo workspace?"
          message="All simulated messages, replies and delivery history are deleted and the sample data is seeded again. Other visitors see the reset too."
          confirmLabel="Reset demo data"
          busy={reset.isPending}
          onConfirm={() => reset.mutate(undefined, { onSettled: () => setConfirmReset(false) })}
          onCancel={() => setConfirmReset(false)}
        />
      ) : null}
    </div>
  )
}
