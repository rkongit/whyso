import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { buildCommands, filterCommands, type PaletteCommand } from '../commands'
import { useAppData } from '../AppData'

interface Props {
  open: boolean
  onClose: () => void
}

export function CommandPalette({ open, onClose }: Props) {
  const navigate = useNavigate()
  const { refresh, setToast } = useAppData()
  const commands = useMemo(() => buildCommands(), [])
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const [pending, setPending] = useState<PaletteCommand | null>(null)
  const [argValues, setArgValues] = useState<Record<string, string>>({})
  const [argIndex, setArgIndex] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const filtered = useMemo(() => filterCommands(commands, query), [commands, query])

  useEffect(() => {
    if (!open) {
      setQuery('')
      setActive(0)
      setPending(null)
      setArgValues({})
      setArgIndex(0)
      setError(null)
      setBusy(false)
    }
  }, [open])

  useEffect(() => {
    setActive(0)
  }, [query])

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
    if (cmd.confirm && !window.confirm(cmd.confirm)) return
    setBusy(true)
    setError(null)
    try {
      await cmd.run(args, helpers)
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  function selectCommand(cmd: PaletteCommand) {
    if (cmd.args && cmd.args.length > 0) {
      setPending(cmd)
      setArgValues({})
      setArgIndex(0)
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
        aria-label="Command palette"
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.preventDefault()
            if (pending) {
              setPending(null)
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
            <input
              className="palette-input"
              autoFocus
              placeholder="Type a command… (status, tree, why, link, rebuild)"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
            <div className="palette-list">
              {filtered.length === 0 && <div className="palette-empty">No matching commands</div>}
              {filtered.map((cmd, i) => (
                <button
                  key={cmd.id}
                  type="button"
                  className={`palette-item${i === active ? ' active' : ''}`}
                  onMouseEnter={() => setActive(i)}
                  onClick={() => selectCommand(cmd)}
                >
                  <span className="title">{cmd.title}</span>
                  <span className="sub">{cmd.cli}</span>
                </button>
              ))}
            </div>
          </>
        ) : (
          <>
            <div className="palette-input" style={{ fontWeight: 600 }}>
              {pending.title}
            </div>
            <div className="palette-list">
              <div className="muted" style={{ padding: '4px 10px' }}>
                {pending.cli}
              </div>
              {pending.args!.map((arg, i) => (
                <div key={arg.name} className="prompt-row" style={{ opacity: i === argIndex ? 1 : 0.5 }}>
                  <span className="mono" style={{ minWidth: 64 }}>
                    {arg.name}
                  </span>
                  <input
                    className="inline-input"
                    style={{ flex: 1 }}
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
                </div>
              ))}
            </div>
            <div className="prompt-row">
              <button type="button" className="btn" onClick={() => setPending(null)} disabled={busy}>
                Back
              </button>
              <button type="button" className="btn primary" onClick={submitArg} disabled={busy}>
                {argIndex + 1 < (pending.args?.length ?? 0) ? 'Next' : 'Run'}
              </button>
            </div>
          </>
        )}
        {error && <div className="error-banner" style={{ margin: 8 }}>{error}</div>}
      </div>
    </div>
  )
}
