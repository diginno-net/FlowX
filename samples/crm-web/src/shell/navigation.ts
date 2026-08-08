import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'

/**
 * What the application contains, as data.
 *
 * THE NAVIGATION IS A TABLE, NOT A SWITCH. The prototype's shell branches on a `screen` string in
 * forty places; here the rail, the tab strip and the router all read the same array, so a screen
 * that exists is a screen you can reach and a screen you can reach exists. Adding one is a row.
 */

export interface AppDefinition {
  id: string
  /** What the top bar shows when this app is open. */
  label: MessageDescriptor
  /** What the rail shows under the glyph. */
  short: MessageDescriptor
  /** An SVG path, 24×24, stroked. */
  icon: string
  /** Where the rail sends you. */
  to: string
  /** Every route that counts as "inside" this app, for the rail's current marker. */
  owns: readonly string[]
}

export const APPS: readonly AppDefinition[] = [
  {
    id: 'sales',
    label: msg({ message: `Sales Cloud`, context: `the CRM application itself, shown in the top bar` }),
    short: msg({ message: `Sales`, context: `the rail glyph label — must fit under a 24px icon` }),
    icon: 'm3 17 6-6 4 4 8-8M17 7h4v4',
    to: '/',
    owns: ['/', '/records', '/kanban', '/quote'],
  },
  {
    id: 'service',
    label: msg({ message: `Service`, context: `customer support application` }),
    short: msg({ message: `Svc`, context: `rail glyph label for Service` }),
    icon:
      'M3 14v-3a9 9 0 0 1 18 0v3M3 14a2 2 0 0 0 2 2h1v-6H5a2 2 0 0 0-2 2zM21 14a2 2 0 0 1-2 2h-1v-6h1a2 2 0 0 1 2 2zM18 16v1a3 3 0 0 1-3 3h-2',
    to: '/service/cases',
    owns: ['/service'],
  },
  {
    id: 'work',
    label: msg({ message: `My work`, context: `the current person\u2019s own queue` }),
    short: msg({ message: `Work`, context: `rail glyph label for My work` }),
    icon:
      'M22 12h-6l-2 3h-4l-2-3H2M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z',
    to: '/work/inbox',
    owns: ['/work'],
  },
  {
    id: 'exec',
    label: msg({ message: `Executive`, context: `leadership dashboards` }),
    short: msg({ message: `Exec`, context: `rail glyph label for Executive` }),
    icon:
      'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10zM12 13.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z',
    to: '/exec',
    owns: ['/exec'],
  },
  {
    id: 'plan',
    label: msg({ message: `Planning`, context: `account and territory planning` }),
    short: msg({ message: `Plan`, context: `rail glyph label for Planning — a noun, not the verb` }),
    icon: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zm4.24-13.24-2.12 6.36-6.36 2.12 2.12-6.36 6.36-2.12z',
    to: '/plan/portfolio',
    owns: ['/plan'],
  },
  {
    id: 'analytics',
    label: msg({ message: `Analytics`, context: `reports and dashboards` }),
    short: msg({ message: `Rpts`, context: `rail glyph label for Analytics` }),
    icon: 'M3 3v18h18M7 16v-6M12 16V8M17 16v-3',
    to: '/analytics/reports',
    owns: ['/analytics'],
  },
]

export const SETUP_APP: AppDefinition = {
  id: 'setup',
  label: msg({ message: `Setup`, context: `administration` }),
  short: msg({ message: `Setup`, context: `rail glyph label for Setup` }),
  icon: 'M4 6h16M4 12h16M4 18h16M9 4v4M16 10v4M12 16v4',
  to: '/setup',
  owns: ['/setup'],
}

/** The objects the record tabs cover. Plural is what the tab shows. */
export interface ObjectDefinition {
  key: string
  label: MessageDescriptor
  plural: MessageDescriptor
  /** Two letters, for a record's avatar. */
  mono: string
}

export const OBJECTS: readonly ObjectDefinition[] = [
  { key: 'account', label: msg({ message: `Account`, context: `a company this CRM sells to — not a login account` }), plural: msg({ message: `Accounts`, context: `a company this CRM sells to — not a login account` }), mono: 'AC' },
  { key: 'contact', label: msg({ message: `Contact`, context: `a person at an account` }), plural: msg({ message: `Contacts`, context: `a person at an account` }), mono: 'CT' },
  { key: 'lead', label: msg({ message: `Lead`, context: `an unqualified prospect` }), plural: msg({ message: `Leads`, context: `an unqualified prospect` }), mono: 'LD' },
  { key: 'opportunity', label: msg({ message: `Opportunity`, context: `a deal in the pipeline` }), plural: msg({ message: `Opportunities`, context: `a deal in the pipeline` }), mono: 'OP' },
  { key: 'quote', label: msg({ message: `Quote`, context: `a priced proposal` }), plural: msg({ message: `Quotes`, context: `a priced proposal` }), mono: 'QT' },
  { key: 'workorder', label: msg({ message: `Work Order`, context: `field service job` }), plural: msg({ message: `Work Orders`, context: `field service job` }), mono: 'WO' },
  { key: 'task', label: msg({ message: `Task`, context: `a to-do on a record` }), plural: msg({ message: `Tasks`, context: `a to-do on a record` }), mono: 'TK' },
]

export function objectOf(key: string): ObjectDefinition {
  return OBJECTS.find((object) => object.key === key) ?? (OBJECTS[0] as ObjectDefinition)
}

/** Which app a path belongs to, for the rail's current marker. */
export function appForPath(pathname: string): string {
  for (const app of [...APPS, SETUP_APP]) {
    if (app.id === 'sales') continue
    if (app.owns.some((prefix) => pathname === prefix || pathname.startsWith(prefix + '/'))) {
      return app.id
    }
  }

  return 'sales'
}
