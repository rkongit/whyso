import { useSyncExternalStore } from 'react'

export type ThemePref = 'light' | 'dark' | 'system'
export type ResolvedTheme = 'light' | 'dark'

// Keep the key in sync with the inline script in index.html.
const KEY = 'whyso-theme'
const ORDER: ThemePref[] = ['system', 'light', 'dark']

function readPref(): ThemePref {
  try {
    const v = localStorage.getItem(KEY)
    if (v === 'light' || v === 'dark') return v
  } catch {
    // storage unavailable: fall back to the OS setting
  }
  return 'system'
}

const media = window.matchMedia('(prefers-color-scheme: dark)')
const listeners = new Set<() => void>()
let pref = readPref()

function resolve(p: ThemePref): ResolvedTheme {
  if (p === 'system') return media.matches ? 'dark' : 'light'
  return p
}

function apply() {
  document.documentElement.dataset.theme = resolve(pref)
  for (const l of listeners) l()
}

media.addEventListener('change', apply)
apply()

export function setThemePref(next: ThemePref) {
  pref = next
  try {
    if (next === 'system') localStorage.removeItem(KEY)
    else localStorage.setItem(KEY, next)
  } catch {
    // ignore: the choice just won't persist
  }
  apply()
}

function subscribe(cb: () => void) {
  listeners.add(cb)
  return () => {
    listeners.delete(cb)
  }
}

export function useTheme() {
  const snapshot = useSyncExternalStore(subscribe, () => `${pref}:${resolve(pref)}`)
  const [p, r] = snapshot.split(':') as [ThemePref, ResolvedTheme]
  return {
    pref: p,
    resolved: r,
    cycle: () => setThemePref(ORDER[(ORDER.indexOf(p) + 1) % ORDER.length]!),
  }
}
