import type { SVGProps } from 'react'

type Props = SVGProps<SVGSVGElement> & { title?: string }

function Icon({ d, title, className, ...rest }: Props & { d: string }) {
  return (
    <svg className={className ? `i ${className}` : 'i'} viewBox="0 0 24 24" aria-hidden={title ? undefined : true} role={title ? 'img' : undefined} {...rest}>
      {title ? <title>{title}</title> : null}
      <path d={d} />
    </svg>
  )
}

export const IconInbox = (p: Props) => (
  <Icon {...p} d="M3 13.5 5.7 6a1.5 1.5 0 0 1 1.4-1h9.8a1.5 1.5 0 0 1 1.4 1l2.7 7.5V18a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1zM3 13.5h5l1.5 2.5h5l1.5-2.5h5" />
)
export const IconChat = (p: Props) => <Icon {...p} d="M4 5.5h16v10H9.5L5.5 19v-3.5H4zM8 9h8M8 12h5" />
export const IconChart = (p: Props) => <Icon {...p} d="M4 20h16M6 16V10M11 16V5M16 16v-4M20 16V8" />
export const IconSettings = (p: Props) => (
  <Icon
    {...p}
    d="M12 8.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7zM19.4 13.1a7.6 7.6 0 0 0 0-2.2l2-1.5-2-3.4-2.3.9a7.6 7.6 0 0 0-1.9-1.1L14.8 3.3H9.2l-.4 2.5a7.6 7.6 0 0 0-1.9 1.1l-2.3-.9-2 3.4 2 1.5a7.6 7.6 0 0 0 0 2.2l-2 1.5 2 3.4 2.3-.9a7.6 7.6 0 0 0 1.9 1.1l.4 2.5h5.6l.4-2.5a7.6 7.6 0 0 0 1.9-1.1l2.3.9 2-3.4z"
  />
)
export const IconCheck = (p: Props) => <Icon {...p} d="m5 12.5 4.5 4.5L19 7.5" />
export const IconBack = (p: Props) => <Icon {...p} d="M15 5l-7 7 7 7" />
export const IconChevron = (p: Props) => <Icon {...p} d="m9 5 7 7-7 7" />
export const IconCopy = (p: Props) => <Icon {...p} d="M9 9h10v11H9zM5 15V4h10" />
export const IconSearch = (p: Props) => <Icon {...p} d="M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM15.5 15.5 20 20" />
export const IconRefresh = (p: Props) => <Icon {...p} d="M4.5 12a7.5 7.5 0 1 0 2.2-5.3M4.5 4.5v4h4" />
export const IconSun = (p: Props) => <Icon {...p} d="M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8zM12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4" />
export const IconMoon = (p: Props) => <Icon {...p} d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z" />
export const IconLogout = (p: Props) => <Icon {...p} d="M10 4H5v16h5M14 8l4 4-4 4M18 12H9" />
export const IconX = (p: Props) => <Icon {...p} d="M6 6l12 12M18 6 6 18" />
export const IconAlert = (p: Props) => <Icon {...p} d="M12 3.8 21.2 19.5H2.8zM12 10v4.2M12 16.9v.2" />
export const IconLock = (p: Props) => <Icon {...p} d="M5 11h14v9H5zM8 11V8a4 4 0 0 1 8 0v3" />
export const IconExternal = (p: Props) => <Icon {...p} d="M14 4h6v6M20 4l-9 9M18 13v7H4V6h7" />
export const IconPlus = (p: Props) => <Icon {...p} d="M12 5v14M5 12h14" />
export const IconKey = (p: Props) => <Icon {...p} d="M8 10a4 4 0 1 0 0 8 4 4 0 0 0 0-8zM11 13l9-9M16 8l3 3M14 10l3 3" />
export const IconTrash = (p: Props) => <Icon {...p} d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6" />
export const IconSend = (p: Props) => <Icon {...p} d="M20.5 3.5 10 14M20.5 3.5 14 20.5l-4-6.5-6.5-4z" />
export const IconReplay = (p: Props) => <Icon {...p} d="M4.5 12a7.5 7.5 0 1 0 2.2-5.3M4.5 4.5v4h4M10 9.5v5l4-2.5z" />
export const IconGithub = (p: Props) => (
  <Icon
    {...p}
    d="M12 2.5a9.5 9.5 0 0 0-3 18.5c.5.1.7-.2.7-.5v-1.8c-2.7.6-3.3-1.2-3.3-1.2-.4-1.1-1-1.4-1-1.4-.9-.6.1-.6.1-.6 1 .1 1.5 1 1.5 1 .9 1.5 2.3 1.1 2.9.8.1-.6.3-1.1.6-1.3-2.1-.2-4.4-1.1-4.4-4.8 0-1 .4-1.9 1-2.6-.1-.2-.4-1.2.1-2.5 0 0 .8-.3 2.6 1a9 9 0 0 1 4.8 0c1.8-1.3 2.6-1 2.6-1 .5 1.3.2 2.3.1 2.5.6.7 1 1.6 1 2.6 0 3.7-2.3 4.6-4.4 4.8.4.3.7.9.7 1.8v2.7c0 .3.2.6.7.5A9.5 9.5 0 0 0 12 2.5z"
  />
)
export const IconDocs = (p: Props) => <Icon {...p} d="M6 3.5h8l4 4v13H6zM14 3.5v4h4M9 12h6M9 15.5h6" />
export const IconArrowRight = (p: Props) => <Icon {...p} d="M4 12h16M14 6l6 6-6 6" />
export const IconClock = (p: Props) => <Icon {...p} d="M12 3.5a8.5 8.5 0 1 0 0 17 8.5 8.5 0 0 0 0-17zM12 7.5V12l3 2" />
export const IconBolt = (p: Props) => <Icon {...p} d="M13 2.5 5 13.5h6l-1 8 9-11.5h-6z" />

export function Logo({ className }: { className?: string }) {
  return (
    <svg className={className ? `logo ${className}` : 'logo'} viewBox="0 0 28 28" aria-hidden="true">
      <path d="M3 7h6.5l5 7M3 21h6.5l5-7M3 14h16.5" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="23.2" cy="14" r="3" fill="currentColor" />
    </svg>
  )
}
