import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ArrowLeft, ArrowRight, CircleAlert, Search, TextCursorInput, Zap, type LucideIcon } from 'lucide-react'
import { buildCommands, filterCommands, type CommandKind, type PaletteCommand } from '../commands'
import { useAppData } from '../AppData'
import { errorMessage } from '../format'
import { useConfirm } from '../confirm'

interface Props {
  open: boolean
  onClose: () => void
}

const KIND_META: Record<CommandKind, { group: string; icon: LucideIcon }> = {
  navigate: { group: 'Go to', icon: ArrowRight },
  query: { group: 'Query', icon: TextCursorInput },
  mutate: { group: 'Actions', icon: Zap },
}

export function CommandPalette({ open, onClose }: Props) {
  const navigate = useNavigate()
  const confirm = useConfirm()
  const { refresh, setToast } = useAppData()
  const commands = useMemo(() => buildCommands(), [])
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const [pending, setPending] = useState<PaletteCommand | null>(null)
  const [argValues, setArgValues] = useState<Record<string, string>>({})
  const [argIndex, setArgIndex] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const returnFocus = useRef<HTMLElement | null>(null)

  const filtered = useMemo(() => filterCommands(commands, query), [commands, query])

  useEffect(() => {
    if (open) {
      returnFocus.current = document.activeElement as HTMLElement | null
      return
    }
    setQuery('')
    setActive(0)
    setPending(null)
    setArgValues({})
    setArgIndex(0)
    setError(null)
    setBusy(false)
    returnFocus.current?.focus?.()
    returnFocus.current = null
  }, [open])

  useEffect(() => {
    setActive(0)
  }, [query])

  useEffect(() => {
    listRef.current?.querySelector('.palette-item.active')?.scrollIntoView({ block: 'nearest' })
  }, [active])

  if (!open) return null

  const helpers = {
    navigate: (to: string) => {
      navigate(to)
      onClose()
    },
    refresh,
    toast: (msg: string) => setToast(msg),
    promptValues: argValues,
  }

  async function runCommand(cmd: PaletteCommand, args: Record<string, string>) {
    if (cmd.confirm && !(await confirm({ title: cmd.title, message: cmd.confirm }))) return
    setBusy(true)
    setError(null)
    try {
      await cmd.run(args, helpers)
      onClose()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  function selectCommand(cmd: PaletteCommand) {
    if (cmd.args && cmd.args.length > 0) {
      setPending(cmd)
      setArgValues({})
      setArgIndex(0)
      setError(null)
      return
    }
    void runCommand(cmd, {})
  }

  function submitArg() {
    if (!pending?.args) return
    const spec = pending.args[argIndex]!
    const value = (argValues[spec.name] ?? '').trim()
    if (!value && !spec.optional) {
      setError(`${spec.name} is required`)
      return
    }
    if (argIndex + 1 < pending.args.length) {
      setArgIndex(argIndex + 1)
      setError(null)
      return
    }
    void runCommand(pending, argValues)
  }

  const isLastArg = argIndex + 1 >= (pending?.args?.length ?? 0)

  return (
    <div
      className="palette-backdrop"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose()
      }}
    >
      <div
        className="palette"
        role="dialog"
        aria-modal="true"
        aria-label="Command palette"
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.preventDefault()
            if (pending) {
              setPending(null)
              setError(null)
              return
            }
            onClose()
          }
          if (pending) return
          if (e.key === 'ArrowDown') {
            e.preventDefault()
            setActive((i) => Math.min(i + 1, Math.max(0, filtered.length - 1)))
          }
          if (e.key === 'ArrowUp') {
            e.preventDefault()
            setActive((i) => Math.max(i - 1, 0))
          }
          if (e.key === 'Enter') {
            e.preventDefault()
            const cmd = filtered[active]
            if (cmd) selectCommand(cmd)
          }
        }}
      >
        {!pending ? (
          <>
            <div className="palette-head">
              <Search size={17} />
              <input
                autoFocus
                aria-label="Command"
                placeholder="Type a command… (status, tree, why, link, rebuild)"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
              />
            </div>
            <div className="palette-list" ref={listRef}>
              {filtered.length === 0 && <div className="palette-empty">No matching commands</div>}
              {filtered.map((cmd, i) => {
                const meta = KIND_META[cmd.kind]
                const Icon = meta.icon
                const startsGroup = i === 0 || filtered[i - 1]!.kind !== cmd.kind
                return (
                  <div key={cmd.id}>
                    {startsGroup && <div className="palette-group">{meta.group}</div>}
                    <button
                      type="button"
                      className={`palette-item${i === active ? ' active' : ''}`}
                      onMouseMove={() => setActive(i)}
                      onClick={() => selectCommand(cmd)}
                    >
                      <Icon size={15} />
                      <span className="title">{cmd.title}</span>
                      <span className="sub">{cmd.cli}</span>
                    </button>
                  </div>
                )
              })}
            </div>
            <div className="palette-foot">
              <span className="hint">
                <kbd>↑</kbd>
                <kbd>↓</kbd> navigate
              </span>
              <span className="hint">
                <kbd>Enter</kbd> run
              </span>
              <span className="hint">
                <kbd>Esc</kbd> close
              </span>
            </div>
          </>
        ) : (
          <>
            <div className="palette-head">
              <button
                type="button"
                className="copy-btn"
                aria-label="Back to commands"
                onClick={() => {
                  setPending(null)
                  setError(null)
                }}
                disabled={busy}
              >
                <ArrowLeft size={16} />
              </button>
              <span className="title">{pending.title}</span>
              <span className="mono">{pending.cli}</span>
            </div>
            <div className="palette-args">
              {pending.args!.map((arg, i) => (
                <label key={arg.name} className={`palette-arg${i === argIndex ? '' : ' inactive'}`}>
                  <span className="mono">{arg.name}</span>
                  <input
                    // Remount when it becomes the current step so autoFocus applies.
                    key={i === argIndex ? 'current' : 'idle'}
                    className="input"
                    autoFocus={i === argIndex}
                    disabled={i !== argIndex || busy}
                    placeholder={arg.placeholder + (arg.optional ? ' (optional)' : '')}
                    value={argValues[arg.name] ?? ''}
                    onChange={(e) =>
                      setArgValues((prev) => ({ ...prev, [arg.name]: e.target.value }))
                    }
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault()
                        submitArg()
                      }
                    }}
                  />
                </label>
              ))}
            </div>
            {error && (
              <div className="banner" role="alert">
                <CircleAlert size={15} />
                <span>{error}</span>
              </div>
            )}
            <div className="palette-foot">
              <span className="hint">
                <kbd>Enter</kbd> {isLastArg ? 'run' : 'next'}
              </span>
              <span className="hint">
                <kbd>Esc</kbd> back
              </span>
              <div className="actions">
                <button type="button" className="btn sm primary" onClick={submitArg} disabled={busy}>
                  {isLastArg ? 'Run' : 'Next'}
                </button>
              </div>
            </div>
          </>
        )}
        {!pending && error && (
          <div className="banner" role="alert">
            <CircleAlert size={15} />
            <span>{error}</span>
          </div>
        )}
      </div>
    </div>
  )
}
