import { Link } from 'react-router-dom'
import { ChannelGlyph } from '../components/Chips'
import { IconCheck, IconDocs, IconGithub, Logo } from '../components/Icons'
import { ThemeToggle } from '../components/ThemeToggle'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { API_BASE } from '../lib/api'
import { useAuth } from '../lib/auth'

export const GITHUB_URL = 'https://github.com/thongminhtran/relay-hub'

const PITCH =
  'Every customer message in one inbox — Instagram, Facebook, website form, email and Telegram — on an integration engine with retries, dead-letter and replay.'

const SAMPLE_ROWS: { channel: 'instagram' | 'facebook' | 'web' | 'telegram' | 'email'; name: string; text: string; time: string; to: string }[] = [
  { channel: 'telegram', name: 'Minh Tran', text: 'Can I order 3 lemongrass chicken bánh mì for pickup in 30 minutes?', time: 'just now', to: 'Discord' },
  { channel: 'instagram', name: 'Linh Nguyen', text: 'Do you have vegan options? Bringing 4 friends on Saturday!', time: '4 min ago', to: 'Discord' },
  { channel: 'web', name: 'Sarah Mitchell', text: 'Catering quote for a team lunch on Oct 23, about 40 people…', time: '18 min ago', to: 'Discord + email' },
  { channel: 'facebook', name: 'Daniel Okafor', text: 'Are you open on Thanksgiving Monday?', time: '1 h ago', to: 'Discord' },
  { channel: 'email', name: 'Rachel Kim', text: 'Do you sell gift cards? Looking for five $25 cards for my staff.', time: '3 h ago', to: 'Teams' },
]

const HOW = [
  {
    k: 'Step 1 · Ingest',
    title: 'Signed webhooks, answered in milliseconds',
    text: 'Meta, Telegram, the email worker and your website form post to one API. Each request is verified (HMAC, bot secret or API key) and dropped on a Storage Queue — the hot path never touches SQL. In the demo, one click simulates a correctly signed Instagram, Facebook, Telegram, email or website-form webhook through that same path.',
    tags: ['X-Hub-Signature-256', 'X-Api-Key', 'Storage Queues', '202 Accepted'],
  },
  {
    k: 'Step 2 · Process',
    title: 'A worker that survives duplicates and outages',
    text: 'Azure Functions parse the payload, dedupe on (source, external id), store the event and the conversation, then create one delivery per route. Failed deliveries retry with backoff and dead-letter after five attempts. The demo has a "Simulate failure" switch: flip it and watch a delivery retry at 2, 4, 8, 16 and 32 s, dead-letter, then replay it.',
    tags: ['Azure Functions', 'EF Core', 'idempotent', '30 s → 1 h backoff'],
  },
  {
    k: 'Step 3 · Notify & reply',
    title: 'Your team sees it, and answers from the same screen',
    text: 'Routes push notifications to Discord, Teams, email, Telegram or any HTTPS endpoint. The dashboard updates over SignalR and lets you reply inside the 24-hour window. Replay anything that failed with one click.',
    tags: ['SignalR', 'Meta Send API', 'Replay', 'OpenTelemetry'],
  },
]

const STACK = ['.NET 10', 'Azure Functions', 'Azure SQL', 'Storage Queues', 'SignalR', 'React', 'Bicep', 'GitHub Actions', 'OpenTelemetry']

export function LandingPage() {
  useDocumentTitle(null)
  const { isAuthenticated } = useAuth()
  const demoHref = isAuthenticated ? '/app' : '/app?demo=1'

  return (
    <div className="landing">
      <nav className="l-nav" aria-label="Site">
        <Link to="/" className="brand">
          <Logo />
          <span>RelayHub</span>
        </Link>
        <div className="l-links">
          <a href="#how">How it works</a>
          <a href="#architecture">Architecture</a>
          <a href="#stack">Tech stack</a>
          <a href={GITHUB_URL} target="_blank" rel="noreferrer">
            GitHub
          </a>
          <ThemeToggle className="btn btn-sm btn-quiet btn-icon" />
        </div>
        <Link to={demoHref} className="btn btn-primary btn-sm">
          Try the live demo
        </Link>
      </nav>

      <section className="hero">
        <div>
          <p className="eyebrow">Unified inbox · Integration hub</p>
          <h1>Every customer message in one inbox. None of them lost.</h1>
          <p className="lede">{PITCH}</p>
          <div className="hero-cta">
            <Link to={demoHref} className="btn btn-primary btn-lg">
              Try the live demo
            </Link>
            <Link to="/login" className="btn btn-lg">
              Owner login
            </Link>
          </div>
          <p className="hero-note">No sign-up · nothing to install · simulate messages and outages in a sandbox workspace with mocked destinations</p>
        </div>
        <div className="live-card" role="img" aria-label="Example inbox receiving messages from several channels">
          <div className="live-head">
            <b>Inbox · Prairie Bánh Mì</b>
            <span className="live-dot">Live</span>
          </div>
          <ul className="live-rows">
            {SAMPLE_ROWS.map((r) => (
              <li className="live-row" key={r.name}>
                <ChannelGlyph channel={r.channel} />
                <span className="nm">{r.name}</span>
                <span className="tm">{r.time}</span>
                <span className="tx">{r.text}</span>
                <span className="rt">
                  <IconCheck /> {r.to}
                </span>
              </li>
            ))}
          </ul>
          <div className="live-foot">
            <span>In: Instagram · Facebook · website form · email · Telegram</span>
            <span>Out: Discord · Teams · email · Telegram · any HTTPS API</span>
          </div>
        </div>
      </section>

      <section className="l-sec" id="how">
        <p className="eyebrow">How it works</p>
        <h2>Ingest, process, notify — and reply</h2>
        <p className="sub">
          The API answers right away and the slow work happens behind a queue, so a sleeping database or a broken destination never loses a
          message.
        </p>
        <div className="how">
          {HOW.map((h) => (
            <article key={h.k}>
              <span className="k">{h.k}</span>
              <h3>{h.title}</h3>
              <p>{h.text}</p>
              <ul aria-label="Technologies">
                {h.tags.map((t) => (
                  <li key={t}>{t}</li>
                ))}
              </ul>
            </article>
          ))}
        </div>
      </section>

      <section className="l-sec" id="architecture">
        <p className="eyebrow">Architecture</p>
        <h2>Queue-first on Azure free tiers</h2>
        <p className="sub">
          App Service hosts the API, Flex Consumption Functions run the worker, Azure SQL serverless stores the data, Storage Queues carry the
          work and SignalR pushes updates to this dashboard. Infrastructure is Bicep, deployed by GitHub Actions with OIDC — no stored secrets.
        </p>
        <figure className="arch">
          <img src="/architecture.png" alt="RelayHub architecture: channels post signed webhooks to the API, which enqueues to Storage Queues; Azure Functions store events in Azure SQL, deliver to destinations with retries and dead-letter, and notify the React dashboard through SignalR." width={2376} height={1410} loading="lazy" />
          <figcaption>One message end to end: webhook → queue → worker → SQL → SignalR → delivery with retries and replay.</figcaption>
        </figure>
      </section>

      <section className="l-sec" id="stack">
        <p className="eyebrow">Tech stack</p>
        <h2>.NET and Azure on the back end, React and TypeScript on the front</h2>
        <ul className="stack-badges" aria-label="Technologies">
          {STACK.map((s) => (
            <li key={s}>{s}</li>
          ))}
        </ul>
        <p className="stack-note">
          Runs on Azure free tiers for about $0 a month. The REST API is documented with OpenAPI and Scalar — call it yourself.
        </p>
        <div className="hero-cta">
          <a className="btn" href={GITHUB_URL} target="_blank" rel="noreferrer">
            <IconGithub /> Source on GitHub
          </a>
          <a className="btn" href={`${API_BASE}/docs`} target="_blank" rel="noreferrer">
            <IconDocs /> API docs
          </a>
        </div>
      </section>

      <section className="cta-band">
        <div>
          <h2>See the whole flow in under five minutes</h2>
          <p>Simulate an Instagram DM, reply to it, switch on the failure mode, then replay the dead letter.</p>
        </div>
        <Link to={demoHref} className="btn btn-primary btn-lg">
          Try the live demo
        </Link>
      </section>

      <footer className="l-foot">
        <span>RelayHub · portfolio project by Tim Tran, Calgary</span>
        <span>
          <a href={GITHUB_URL} target="_blank" rel="noreferrer">
            GitHub
          </a>
          {' · '}
          <a href={`${API_BASE}/docs`} target="_blank" rel="noreferrer">
            API docs
          </a>
          {' · '}
          <Link to="/login">Owner login</Link>
        </span>
      </footer>
    </div>
  )
}
