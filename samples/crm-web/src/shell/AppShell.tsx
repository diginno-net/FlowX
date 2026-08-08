import { Trans, useLingui } from '@lingui/react/macro'
import { Link, Outlet, useNavigate, useRouterState } from '@tanstack/react-router'
import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { PERSONAS, useSession } from '@/session/SessionProvider'
import { APPS, OBJECTS, SETUP_APP, appForPath } from './navigation'
import { ProfileDrawer } from './ProfileDrawer'
import styles from './AppShell.module.css'

/**
 * The chrome every screen sits inside: brand and search, the app rail, the tab strip.
 *
 * IT RENDERS ONCE AND NEVER AGAIN. Everything below it is an `<Outlet />`, so moving between
 * screens does not remount the shell — which is what keeps the rail from flashing and the scroll
 * position of a list from being lost on the way to a record and back.
 */
export function AppShell() {
  const navigate = useNavigate()
  const pathname = useRouterState({ select: (state) => state.location.pathname })
  const currentApp = appForPath(pathname)

  // ⌘K anywhere. The prototype draws the hint; a hint for a shortcut that does nothing is worse
  // than no hint, so it is wired.
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        void navigate({ to: '/search' })
      }
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [navigate])

  return (
    <div className={styles.shell}>
      <TopBar currentApp={currentApp} />
      <div className={styles.middle}>
        <AppRail currentApp={currentApp} />
        <div className={styles.main}>
          <TabStrip />
          <main className={styles.content}>
            <Outlet />
          </main>
        </div>
      </div>
    </div>
  )
}

function TopBar({ currentApp }: { currentApp: string }) {
  const { t } = useLingui()
  const [profileOpen, setProfileOpen] = useState(false)
  const session = useSession()
  const navigate = useNavigate()
  const app = [...APPS, SETUP_APP].find((candidate) => candidate.id === currentApp)

  return (
    <header className={styles.topbar}>
      <div className={styles.brand}>
        <div className={styles.mark} aria-hidden="true">
          GoK
        </div>
        <div className={styles.brandRule} />
        <div className={styles.appLabel}>{app ? t(app.label) : ''}</div>
      </div>

      <button type="button" className={styles.search} onClick={() => void navigate({ to: '/search' })}>
        <span className={styles.searchGlyph} aria-hidden="true">
          ⌕
        </span>
        <span>
          <Trans>Search accounts, contacts, opportunities…</Trans>
        </span>
        <span className={styles.kbd} aria-hidden="true">
          ⌘K
        </span>
      </button>

      {/*
        THREE GLYPHS THAT LOOKED LIKE CONTROLS SAT HERE — a notification bell, a home and a help
        mark, drawn from the mock-up, none of them clickable and none of them behind anything.
        A reader who tries one and gets nothing has learnt that this application's chrome does not
        respond, which is the wrong thing to have taught them before they reach a real control.

        THE ROLE PICKER AND THE LANGUAGE CONTROL WENT THE SAME WAY, into the profile the avatar
        opens — two segmented pickers on one strip is one more than the strip has room for, and
        neither is a thing anybody presses twice in a session.
      */}

      <div className={styles.topbarEnd}>
        <button
          type="button"
          className={styles.avatar}
          aria-haspopup="dialog"
          aria-expanded={profileOpen}
          title={t`${session.displayName} · signed in as ${session.persona}`}
          onClick={() => setProfileOpen(true)}
        >
          {session.initials}
        </button>
      </div>

      {profileOpen ? <ProfileDrawer onClose={() => setProfileOpen(false)} /> : null}
    </header>
  )

}

function AppRail({ currentApp }: { currentApp: string }) {
  const { t } = useLingui()
  const session = useSession()

  return (
    <nav className={styles.rail} aria-label={t`Applications`}>
      {APPS.map((app) => (
        <RailLink key={app.id} to={app.to} label={t(app.label)} current={currentApp === app.id}>
          <RailIcon path={app.icon} />
          <span className={styles.railLabel}>{t(app.short)}</span>
        </RailLink>
      ))}

      <div className={styles.railFoot}>
        <div className={styles.railRule} />
        <RailLink
          to={SETUP_APP.to}
          label="Setup and configuration"
          current={currentApp === 'setup'}
        >
          <RailIcon path={SETUP_APP.icon} />
          <span className={styles.railLabel}>Setup</span>
        </RailLink>
        <div className={styles.railButton} style={{ height: 52, cursor: 'default' }}>
          <span className={styles.railAvatar}>{session.initials}</span>
          {/*
            The chair's own label, from the one list that has them. A chain of three comparisons
            ending in `: 'Admin'` called every persona it did not name an administrator — which
            the Contoso seller was, in the rail, while holding a reader's grants.
          */}
          <span className={styles.railLabel} style={{ color: 'var(--color-neutral-600)' }}>
            {PERSONAS.find((persona) => persona.id === session.persona)?.label ?? session.persona}
          </span>
        </div>
      </div>
    </nav>
  )
}

function RailLink({
  to,
  label,
  current,
  children,
}: {
  to: string
  label: string
  current: boolean
  children: ReactNode
}) {
  return (
    <Link
      to={to}
      title={label}
      aria-label={label}
      aria-current={current ? 'page' : undefined}
      className={styles.railButton}
    >
      {children}
    </Link>
  )
}

function RailIcon({ path }: { path: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      width="20"
      height="20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      style={{ display: 'block' }}
    >
      <path d={path} />
    </svg>
  )
}

function TabStrip() {
  const { t } = useLingui()
  const { tenantId } = useSession()
  const pathname = useRouterState({ select: (state) => state.location.pathname })

  return (
    <nav className={styles.tabs} aria-label={t`Record types`}>
      <Link to="/" className={styles.tab} aria-current={pathname === '/' ? 'page' : undefined}>
        <Trans>Home</Trans>
      </Link>
      {OBJECTS.map((object) => (
        <Link
          key={object.key}
          to="/records/$object"
          params={{ object: object.key }}
          className={styles.tab}
          aria-current={pathname.startsWith(`/records/${object.key}`) ? 'page' : undefined}
        >
          {t(object.plural)}
        </Link>
      ))}
      <Link
        to="/analytics/reports"
        className={cx(styles.tab)}
        aria-current={pathname.startsWith('/analytics') ? 'page' : undefined}
      >
        <Trans>Reports</Trans>
      </Link>
      <Link
        to="/setup"
        className={styles.tab}
        aria-current={pathname.startsWith('/setup') ? 'page' : undefined}
      >
        <Trans>Setup</Trans>
      </Link>

      {/*
        THE ORGANISATION THIS IS, NOT A NAME FROM THE MOCK-UP. It read "org: gridline-prod" and
        "sandbox" on every tenant — an organisation nobody is signed in to, beside a badge saying
        this is not the real one. Both were fixed strings, and the second is the sort of label a
        reader trusts when deciding whether an action is safe. The tenant is what the token
        carries and it is what every request on the screen is scoped by.
      */}
      <div className={styles.org}>
        <span className={styles.orgName}>org: {tenantId}</span>
      </div>
    </nav>
  )
}
