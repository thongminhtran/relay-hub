import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ChannelGlyph } from '../components/Chips'
import { IconBack, IconSend } from '../components/Icons'
import { LiveIndicator } from '../components/LiveIndicator'
import { EmptyState, ErrorState, Notice, Skeleton } from '../components/Primitives'
import { ReplyCountdown } from '../components/ReplyCountdown'
import { useCountdown } from '../hooks/useCountdown'
import { useConversation } from '../hooks/useData'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { isApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { formatDay, formatFull, formatTime } from '../lib/format'
import { channelLabel } from '../lib/labels'
import { keys, mutations } from '../lib/queries'
import { useToast } from '../lib/toast'
import type { ConversationDetail, ConversationMessage } from '../lib/types'

export const REPLY_MAX = 2000

interface PendingReply {
  tempId: number
  text: string
  status: 'sending' | 'sent' | 'sentDemo'
  at: string
  messageId?: number
}

export function ConversationThreadPage() {
  const params = useParams()
  const id = Number(params.id)
  const query = useConversation(Number.isFinite(id) ? id : null)
  const convo = query.data
  const title = convo ? convo.senderName || convo.senderHandle || `Conversation #${id}` : 'Conversation'
  useDocumentTitle(title)

  return (
    <>
      <Link to="/app/conversations" className="back">
        <IconBack /> Conversations
      </Link>
      {query.isPending ? (
        <ThreadSkeleton />
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} title={isApiError(query.error) && query.error.status === 404 ? 'Conversation not found' : undefined} />
      ) : convo ? (
        <Thread convo={convo} />
      ) : null}
    </>
  )
}

function ThreadSkeleton() {
  return (
    <section className="panel" aria-busy="true" aria-label="Loading conversation">
      <div className="d-head">
        <div className="d-who">
          <Skeleton width={40} height={40} />
          <div className="stack-sm">
            <Skeleton width={160} height={18} />
            <Skeleton width={220} />
          </div>
        </div>
      </div>
      <div className="panel-body">
        <div className="thread">
          <Skeleton width="60%" height={44} className="bubble in" />
          <Skeleton width="45%" height={44} className="bubble out" />
          <Skeleton width="70%" height={44} className="bubble in" />
        </div>
      </div>
    </section>
  )
}

function Thread({ convo }: { convo: ConversationDetail }) {
  const { isDemo } = useAuth()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [text, setText] = useState('')
  const [pending, setPending] = useState<PendingReply[]>([])
  const endRef = useRef<HTMLDivElement>(null)
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  const countdown = useCountdown(convo.reply.windowClosesAt)
  const windowClosed = convo.reply.windowClosesAt !== null && countdown.closed
  const canReply = convo.reply.canReply && !windowClosed
  const closedReason = !convo.reply.canReply
    ? (convo.reply.reason ?? 'Replies are not available for this conversation.')
    : windowClosed
      ? "Meta allows replies only within 24 hours of the customer's last message."
      : null

  // Optimistic bubbles stay until the real message is in the thread (from the reply response or the replySent push);
  // after that, the real bubble carries the "Sent" / "Sent (demo)" label from the matching pending entry.
  const visiblePending = useMemo(() => pending.filter((p) => !p.messageId || !convo.messages.some((m) => m.id === p.messageId)), [pending, convo.messages])
  const sentLabels = useMemo(() => {
    const m = new Map<number, string>()
    for (const p of pending) if (p.messageId) m.set(p.messageId, p.status === 'sentDemo' ? 'Sent (demo)' : 'Sent')
    return m
  }, [pending])

  const messageCount = convo.messages.length + visiblePending.length
  useEffect(() => {
    endRef.current?.scrollIntoView({ block: 'nearest' })
  }, [messageCount])

  const reply = useMutation({
    mutationFn: (vars: { tempId: number; text: string }) => mutations.reply(convo.id, vars.text),
    onSuccess: (res, vars) => {
      setPending((ps) => ps.map((p) => (p.tempId === vars.tempId ? { ...p, status: res.status === 'SentDemo' ? 'sentDemo' : 'sent', messageId: res.messageId, at: res.at } : p)))
      queryClient.setQueryData<ConversationDetail>(keys.conversation(convo.id), (old) => {
        if (!old || old.messages.some((m) => m.id === res.messageId)) return old
        const msg: ConversationMessage = { id: res.messageId, direction: 'out', text: vars.text, at: res.at, providerMessageId: res.providerMessageId, correlationId: null }
        return { ...old, lastOutboundAt: res.at, messages: [...old.messages, msg] }
      })
      void queryClient.invalidateQueries({ queryKey: [...keys.conversations, 'list'] })
      void queryClient.invalidateQueries({ queryKey: keys.events })
      void queryClient.invalidateQueries({ queryKey: keys.stats })
    },
    onError: (err, vars) => {
      setPending((ps) => ps.filter((p) => p.tempId !== vars.tempId))
      setText((t) => (t ? t : vars.text))
      toast.error(err, 'Reply failed')
      if (isApiError(err) && (err.status === 409 || err.status === 501)) void queryClient.invalidateQueries({ queryKey: keys.conversation(convo.id) })
      textareaRef.current?.focus()
    },
  })

  const send = (e?: FormEvent) => {
    e?.preventDefault()
    const body = text.trim()
    if (!body || body.length > REPLY_MAX || !canReply || reply.isPending) return
    const tempId = Date.now()
    setPending((ps) => [...ps, { tempId, text: body, status: 'sending', at: new Date().toISOString() }])
    setText('')
    reply.mutate({ tempId, text: body })
  }

  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') send()
  }

  const who = convo.senderName || convo.senderHandle || 'Unknown sender'
  const over = text.length > REPLY_MAX
  const firstName = who.split(' ')[0]

  return (
    <section className="panel" aria-label={`Conversation with ${who}`}>
      <div className="d-head">
        <div className="d-who">
          <ChannelGlyph channel={convo.channel} size="lg" />
          <div>
            <h2>{who}</h2>
            <p>
              {convo.senderHandle && convo.senderHandle !== who ? `${convo.senderHandle} · ` : ''}
              {channelLabel(convo.channel)} · {convo.sourceName}
              {convo.externalThreadId ? ` · thread ${convo.externalThreadId}` : ''}
            </p>
          </div>
        </div>
        <div className="d-meta">
          <LiveIndicator />
        </div>
      </div>

      <div className="panel-body">
        {convo.messages.length === 0 && visiblePending.length === 0 ? (
          <EmptyState title="No messages in this conversation yet" inPanel />
        ) : (
          <div className="thread" role="log" aria-live="polite" aria-relevant="additions">
            {groupByDay(convo.messages).map((group) => (
              <DayGroup key={group.day} day={group.day} messages={group.messages} channel={convo.channel} sentLabels={sentLabels} />
            ))}
            {visiblePending.map((p) => (
              <div key={p.tempId} className={`bubble out${p.status === 'sending' ? ' is-pending' : ''}`}>
                {p.text}
                <span className="bt">{p.status === 'sending' ? 'Sending…' : p.status === 'sentDemo' ? `${formatTime(p.at)} · Sent (demo)` : `${formatTime(p.at)} · Sent`}</span>
              </div>
            ))}
            <div ref={endRef} />
          </div>
        )}

        <form className={`composer${canReply ? '' : ' is-closed'}`} onSubmit={send}>
          <label className="sr" htmlFor="reply-text">
            Reply to {firstName}
          </label>
          <textarea
            id="reply-text"
            ref={textareaRef}
            rows={2}
            placeholder={canReply ? `Reply to ${firstName} on ${channelLabel(convo.channel)} (Ctrl/⌘ + Enter to send)` : 'Replies are closed for this conversation'}
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={onKey}
            disabled={!canReply || reply.isPending}
            maxLength={REPLY_MAX + 200}
            aria-invalid={over || undefined}
            aria-describedby="reply-note"
          />
          <div className="composer-foot">
            <span className="note" id="reply-note">
              <ReplyCountdown closesAt={convo.reply.windowClosesAt} />
              {closedReason ? (
                <span className="bad-c">{closedReason}</span>
              ) : convo.reply.windowClosesAt ? (
                <span>Meta allows replies for 24 hours after the customer's last message.</span>
              ) : (
                <span>{isDemo ? 'Replies in the demo workspace are mocked — nothing leaves the system.' : `Sends through ${channelLabel(convo.channel)}.`}</span>
              )}
              {isDemo && convo.reply.windowClosesAt && !closedReason ? <span>Demo: the reply is mocked.</span> : null}
            </span>
            <span className={`count num${over ? ' over' : ''}`} aria-live="polite">
              {text.length} / {REPLY_MAX}
            </span>
            <button type="submit" className="btn btn-primary btn-sm" disabled={!canReply || !text.trim() || over || reply.isPending}>
              {reply.isPending ? <span className="spinner" /> : <IconSend />}
              Send reply
            </button>
          </div>
        </form>

        {closedReason && convo.reply.windowClosesAt ? (
          <Notice kind="warn">
            The 24-hour window closed {formatFull(convo.reply.windowClosesAt)}. It reopens when the customer writes again.
          </Notice>
        ) : null}
      </div>
    </section>
  )
}

interface DayGroupData {
  day: string
  messages: ConversationMessage[]
}

function groupByDay(messages: ConversationMessage[]): DayGroupData[] {
  const groups: DayGroupData[] = []
  for (const m of messages) {
    const day = formatDay(m.at)
    const last = groups[groups.length - 1]
    if (last && last.day === day) last.messages.push(m)
    else groups.push({ day, messages: [m] })
  }
  return groups
}

function DayGroup({ day, messages, channel, sentLabels }: { day: string; messages: ConversationMessage[]; channel: string; sentLabels: Map<number, string> }) {
  return (
    <>
      <span className="day-sep">{day}</span>
      {messages.map((m) => (
        <div key={m.id} className={`bubble ${m.direction}`} title={formatFull(m.at)}>
          {m.text}
          <span className="bt">
            {formatTime(m.at)}
            {m.direction === 'out'
              ? ` · ${sentLabels.get(m.id) ?? `sent via ${channelLabel(channel)}${m.providerMessageId?.startsWith('demo-') ? ' (demo)' : ''}`}`
              : ''}
            {m.correlationId ? (
              <>
                {' · '}
                <Link to={`/app/events/${m.id}`} style={{ color: 'inherit', textDecoration: 'underline' }}>
                  details
                </Link>
              </>
            ) : null}
          </span>
        </div>
      ))}
    </>
  )
}
