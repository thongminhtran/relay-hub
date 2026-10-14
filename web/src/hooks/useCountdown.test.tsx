import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReplyCountdown } from '../components/ReplyCountdown'
import { formatCountdown } from '../lib/format'
import { computeCountdown, URGENT_THRESHOLD_MS } from './useCountdown'

describe('computeCountdown / formatCountdown', () => {
  const now = Date.UTC(2026, 9, 14, 12, 0, 0)
  it('has no deadline when windowClosesAt is null', () => {
    expect(computeCountdown(null, now)).toEqual({ remainingMs: null, closed: false, urgent: false })
  })
  it('formats hh:mm:ss and flags the last hour as urgent', () => {
    const inFiveHours = new Date(now + 5 * 3600_000 + 7 * 60_000 + 9_000).toISOString()
    const c = computeCountdown(inFiveHours, now)
    expect(formatCountdown(c.remainingMs!)).toBe('05:07:09')
    expect(c.urgent).toBe(false)
    const in20min = new Date(now + 20 * 60_000).toISOString()
    expect(computeCountdown(in20min, now)).toMatchObject({ urgent: true, closed: false })
    expect(computeCountdown(new Date(now + URGENT_THRESHOLD_MS).toISOString(), now).urgent).toBe(false)
  })
  it('is closed at or after the deadline', () => {
    const c = computeCountdown(new Date(now - 1).toISOString(), now)
    expect(c).toEqual({ remainingMs: 0, closed: true, urgent: false })
    expect(formatCountdown(0)).toBe('00:00:00')
  })
})

describe('<ReplyCountdown>', () => {
  beforeEach(() => vi.useFakeTimers({ now: Date.UTC(2026, 9, 14, 12, 0, 0) }))
  afterEach(() => vi.useRealTimers())

  it('renders nothing without a window', () => {
    const { container } = render(<ReplyCountdown closesAt={null} />)
    expect(container).toBeEmptyDOMElement()
  })

  it('ticks every second, turns amber under one hour and reports closed afterwards', () => {
    const closesAt = new Date(Date.now() + 60 * 60_000 + 2_000).toISOString() // 1 h 2 s left
    render(<ReplyCountdown closesAt={closesAt} />)
    const timer = screen.getByRole('timer')
    expect(timer).toHaveTextContent('01:00:02')
    expect(timer).not.toHaveClass('is-urgent')

    act(() => vi.advanceTimersByTime(3_000))
    expect(timer).toHaveTextContent('00:59:59')
    expect(timer).toHaveClass('is-urgent')

    act(() => vi.advanceTimersByTime(60 * 60_000))
    expect(timer).toHaveTextContent('Window closed')
    expect(timer).toHaveClass('is-closed')
  })
})
