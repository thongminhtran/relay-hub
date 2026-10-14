import { act, render, screen } from '@testing-library/react'
import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { NEXT_ATTEMPT_GRACE_MS, NextAttempt } from './NextAttempt'

describe('<NextAttempt>', () => {
  beforeEach(() => vi.useFakeTimers({ now: Date.UTC(2026, 9, 14, 16, 0, 0) }))
  afterEach(() => vi.useRealTimers())

  it('counts down, then fires onElapsed exactly once after the grace — also under StrictMode double effects', () => {
    const onElapsed = vi.fn()
    const nextAttemptAt = new Date(Date.now() + 4000).toISOString()
    // Call sites key the component on the deadline, so a new `nextAttemptAt` from a refetch remounts it.
    const view = render(
      <StrictMode>
        <NextAttempt key={nextAttemptAt} nextAttemptAt={nextAttemptAt} onElapsed={onElapsed} />
      </StrictMode>,
    )
    expect(screen.getByRole('timer')).toHaveTextContent('in 4s')
    act(() => vi.advanceTimersByTime(2000))
    expect(screen.getByRole('timer')).toHaveTextContent('in 2s')
    act(() => vi.advanceTimersByTime(2000))
    expect(screen.getByRole('timer')).toHaveTextContent('due now')
    expect(onElapsed).not.toHaveBeenCalled()
    act(() => vi.advanceTimersByTime(NEXT_ATTEMPT_GRACE_MS))
    expect(onElapsed).toHaveBeenCalledTimes(1)
    act(() => vi.advanceTimersByTime(60_000))
    expect(onElapsed).toHaveBeenCalledTimes(1)

    // a new deadline from the refetch arms exactly one more follow-up
    const later = new Date(Date.now() + 8000).toISOString()
    view.rerender(
      <StrictMode>
        <NextAttempt key={later} nextAttemptAt={later} onElapsed={onElapsed} />
      </StrictMode>,
    )
    expect(screen.getByRole('timer')).toHaveTextContent('in 8s')
    act(() => vi.advanceTimersByTime(8000)) // reaches zero → React commits "due now" and arms the grace timer
    expect(screen.getByRole('timer')).toHaveTextContent('due now')
    act(() => vi.advanceTimersByTime(NEXT_ATTEMPT_GRACE_MS))
    expect(onElapsed).toHaveBeenCalledTimes(2)
  })

  it('fires once when the deadline is already in the past at mount', () => {
    const onElapsed = vi.fn()
    render(
      <StrictMode>
        <NextAttempt nextAttemptAt={new Date(Date.now() - 1000).toISOString()} onElapsed={onElapsed} />
      </StrictMode>,
    )
    expect(screen.getByRole('timer')).toHaveTextContent('due now')
    act(() => vi.advanceTimersByTime(NEXT_ATTEMPT_GRACE_MS))
    expect(onElapsed).toHaveBeenCalledTimes(1)
    act(() => vi.advanceTimersByTime(60_000))
    expect(onElapsed).toHaveBeenCalledTimes(1)
  })
})
