import { Link, Navigate } from 'react-router-dom'
import { DemoPanel } from '../components/DemoPanel'
import { LiveIndicator } from '../components/LiveIndicator'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { useAuth } from '../lib/auth'

/** `/app/demo` — the Week 5 demo controls on their own page (Demo-workspace users only). */
export function DemoPage() {
  useDocumentTitle('Demo')
  const { isDemo } = useAuth()
  if (!isDemo) return <Navigate to="/app/inbox" replace />
  return (
    <>
      <header className="page-head">
        <div>
          <h1>Demo</h1>
          <p className="desc">
            Everything here runs through the real pipeline: signed webhook → queue → worker → SQL → routes with retries. Destinations and
            replies are mocked in the demo workspace, so nothing reaches a real Discord or Instagram.
          </p>
        </div>
        <div className="actions">
          <LiveIndicator />
        </div>
      </header>
      <DemoPanel />
      <ol className="demo-steps" aria-label="Suggested walk-through">
        <li>
          Press <b>Instagram</b> (or any channel), then open the <Link to="/app/inbox">Inbox</Link>: the message lands within a few seconds with its
          deliveries marked Delivered.
        </li>
        <li>
          Open the conversation and reply — the thread shows <b>Sent (demo)</b> and the 24-hour countdown for Meta channels.
        </li>
        <li>
          Switch <b>Simulate failure</b> on and send a <b>Website form</b> message: its Flaky-endpoint delivery retries at 2, 4, 8, 16 and 32 s and
          then dead-letters. Open the event to watch the countdown between attempts.
        </li>
        <li>
          Switch the failure off and press <b>Replay</b> on the dead-lettered delivery: one more attempt, Delivered. Check <Link to="/app/stats">Stats</Link>.
        </li>
      </ol>
    </>
  )
}
