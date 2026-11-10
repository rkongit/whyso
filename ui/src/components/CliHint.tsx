import { useEffect, useState } from 'react'
import { Check, Copy } from 'lucide-react'

export function CopyButton({ text, label = 'Copy' }: { text: string | (() => string); label?: string }) {
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!copied) return
    const t = window.setTimeout(() => setCopied(false), 1500)
    return () => window.clearTimeout(t)
  }, [copied])

  return (
    <button
      type="button"
      className={`copy-btn${copied ? ' copied' : ''}`}
      title={copied ? 'Copied' : label}
      aria-label={copied ? 'Copied' : label}
      onClick={() => {
        const value = typeof text === 'function' ? text() : text
        navigator.clipboard.writeText(value).then(
          () => setCopied(true),
          () => setCopied(false),
        )
      }}
    >
      {copied ? <Check size={13} /> : <Copy size={13} />}
    </button>
  )
}

/** The CLI equivalent of what the page shows, with a copy button. */
export function CliHint({ command }: { command: string }) {
  return (
    <span className="cli-hint">
      <span className="prompt">$</span>
      <code>{command}</code>
      <CopyButton text={command} label="Copy command" />
    </span>
  )
}
