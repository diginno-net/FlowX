/**
 * Who is signed in, what that lets them do, and what language they read it in.
 *
 * WHY THE ROLE MOVED HERE FROM THE TOP BAR. The bar carried a four-button role picker and, once
 * language arrived, a second run of buttons beside it — two segmented controls competing with the
 * search field for the same strip. Worse, the picker showed the *names* of four personas and
 * nothing about what separates them, which is the one thing this sample exists to demonstrate.
 * Here the grants are listed under the choice, so switching to `manager` and watching
 * `crm.discount.approve` appear is the demonstration rather than a sentence in a README.
 *
 * WHY A DRAWER RATHER THAN A MENU ANCHORED TO THE AVATAR. `Drawer` already closes on Escape,
 * moves focus in on open and returns it on close. A popover would need all three written again,
 * and a second thing in this codebase that opens over the page is a second thing to keep
 * accessible. The panel is what the design system has; this uses it.
 *
 * IT IS NOT A SETTINGS SCREEN. Nothing here writes to the server — the persona is a client-side
 * token swap and the locale is a `localStorage` key. A real deployment replaces the first with
 * whatever its identity provider says and keeps the second.
 */
import { Trans, useLingui } from '@lingui/react/macro'
import { useNavigate } from '@tanstack/react-router'
import { Button, ButtonGroup } from '@/design/primitives/Button'
import { Drawer } from '@/design/primitives/Drawer'
import { Tag } from '@/design/primitives/Tag'
import { useLocale } from '@/app/LocaleProvider'
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

  /**
   * Switching role also lands where that role starts, as the prototype does — a director dropped
   * on a seller's console has to navigate before they see anything of theirs. This moved here
   * with the picker; leaving it behind in the top bar would have made the switch land nowhere.
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
    <Drawer
      title={session.displayName}
      eyebrow={t`Profile`}
      subtitle={t`Signed in to ${session.tenantId}`}
      onClose={onClose}
      width={420}
    >
      <section className={styles.section}>
        <h3 className={styles.heading}>
          <Trans>Role</Trans>
        </h3>
        <p className={styles.note}>
          <Trans>
            The sample mints one token per role. Switching changes which token every request
            carries — not what the screen hides.
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
