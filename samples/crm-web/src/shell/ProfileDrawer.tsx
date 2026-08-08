/**
 * Who is signed in, what the tenant knows about them, what that lets them do, and what language
 * they read it in.
 *
 * THE IDENTITY IS THE TENANT'S, NOT THE CLIENT'S. It read a display name off a constant in
 * `SessionProvider` and stopped there, so the panel could tell you your own initials and nothing
 * a colleague could have told you: not your title, not who you report to, not how many people
 * report to you. `org_member` holds all three and `crm.org.chart` already serves them — the panel
 * was asking the wrong side of the connection.
 *
 * WHY THE ROLE PICKER IS HERE AND LOOKS LIKE A SETTING. It is the sample's stand-in for signing in
 * as somebody else: there is no login, and `CrmTokenHandler` says so in its own header — the
 * tokens are constants and a real deployment deletes the file. Rather than dress that up, the
 * panel states it under the picker. A "Sign out" button would be the alternative and it would do
 * nothing, which is the one control this codebase argues hardest against.
 *
 * NOTHING HERE WRITES TO THE SERVER. The persona is a token swap in the client and the locale is a
 * `localStorage` key, and the panel says so where a reader would otherwise assume their language
 * had been saved to their account.
 */
import { Trans, useLingui } from '@lingui/react/macro'
import { useNavigate } from '@tanstack/react-router'
import { Button, ButtonGroup } from '@/design/primitives/Button'
import { Drawer } from '@/design/primitives/Drawer'
import { Tag } from '@/design/primitives/Tag'
import { useLocale } from '@/app/LocaleProvider'
import { useOrgChart } from '@/api/queries/hooks'
import { LOCALES } from '@/lib/i18n'
import type { Locale } from '@/lib/i18n'
import { PERSONAS, useSession } from '@/session/SessionProvider'
import type { Persona } from '@/session/SessionProvider'
import styles from './ProfileDrawer.module.css'

export function ProfileDrawer({ onClose }: { onClose: () => void }) {
  const session = useSession()
  const { locale, setLocale } = useLocale()
  const { t } = useLingui()
  const navigate = useNavigate()
  const org = useOrgChart()

  const members = org.data?.members ?? []
  const me = members.find((member) => member.userId === session.userId)
  const manager = me?.reportsTo ? members.find((m) => m.userId === me.reportsTo) : undefined

  /**
   * Switching role also lands where that role starts, as the prototype does — a director dropped
   * on a seller's console has to navigate before they see anything of theirs.
   */
  function switchTo(persona: Persona) {
    session.switchTo(persona)

    const home =
      persona === 'admin'
        ? '/setup'
        : persona === 'director'
          ? '/exec'
          : persona === 'manager'
            ? '/plan/portfolio'
            : '/'

    void navigate({ to: home })
    onClose()
  }

  return (
    <Drawer title={t`Profile`} onClose={onClose} width={420}>
      <section className={styles.identity}>
        <div className={styles.avatar} aria-hidden="true">
          {session.initials}
        </div>
        <div className={styles.identityText}>
          <h2 className={styles.name}>{session.displayName}</h2>
          {/*
            The tenant's word for what this person is, which is not the same as the grants they
            hold: `org_member.role` decides what they see by default, and the token decides what
            they may do. Both are shown because a reader who is told only one asks about the other.
          */}
          <div className={styles.title}>
            {me ? me.role : <span className={styles.unknown}><Trans>Not in the org chart</Trans></span>}
          </div>
          <code className={styles.userId}>{session.userId}</code>
        </div>
      </section>

      <dl className={styles.meta}>
        <div>
          <dt><Trans>Organisation</Trans></dt>
          <dd>{session.tenantId}</dd>
        </div>
        <div>
          <dt><Trans>Reports to</Trans></dt>
          <dd>{manager ? manager.displayName : <span className={styles.unknown}>—</span>}</dd>
        </div>
        <div>
          <dt><Trans>People reporting</Trans></dt>
          <dd>{me ? me.reports : <span className={styles.unknown}>—</span>}</dd>
        </div>
        <div>
          <dt><Trans>Signed in as</Trans></dt>
          <dd>{PERSONAS.find((persona) => persona.id === session.persona)?.label ?? session.persona}</dd>
        </div>
      </dl>

      <section className={styles.section}>
        <h3 className={styles.heading}>
          <Trans>Sign in as</Trans>
        </h3>
        <p className={styles.note}>
          <Trans>
            This build has no login. Each role is a token the client sends, so switching here is
            what signing in as somebody else would be — the server sees a different caller, not a
            screen with fewer buttons.
          </Trans>
        </p>
        <ButtonGroup label={t`Signed-in role`}>
          {PERSONAS.map((persona) => (
            <Button
              key={persona.id}
              aria-pressed={session.persona === persona.id}
              onClick={() => switchTo(persona.id)}
            >
              {persona.label}
            </Button>
          ))}
        </ButtonGroup>
      </section>

      <section className={styles.section}>
        <h3 className={styles.heading}>
          <Trans>What this role holds</Trans>
        </h3>
        {session.permissions.length === 0 ? (
          <p className={styles.note}>
            <Trans>No grants. Every write is refused.</Trans>
          </p>
        ) : (
          <ul className={styles.grants}>
            {session.permissions.map((permission) => (
              <li key={permission}>
                <Tag>{permission}</Tag>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className={styles.section}>
        <h3 className={styles.heading}>
          <Trans>Language</Trans>
        </h3>
        <p className={styles.note}>
          <Trans>
            Applies to this browser only. Record data keeps the language it was written in.
          </Trans>
        </p>
        <ButtonGroup label={t`Language`}>
          {(Object.entries(LOCALES) as [Locale, string][]).map(([code, name]) => (
            <Button key={code} lang={code} aria-pressed={locale === code} onClick={() => setLocale(code)}>
              {name}
            </Button>
          ))}
        </ButtonGroup>
      </section>
    </Drawer>
  )
}
