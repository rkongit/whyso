import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { ConfirmContext, type ConfirmOptions } from '../confirm'

interface Pending extends ConfirmOptions {
  resolve: (ok: boolean) => void
}

export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null)
  const ref = useRef<HTMLDialogElement>(null)

  const confirm = useCallback(
    (opts: ConfirmOptions) =>
      new Promise<boolean>((resolve) => {
        setPending({ ...opts, resolve })
      }),
    [],
  )

  useEffect(() => {
    const el = ref.current
    if (pending && el && !el.open) el.showModal()
  }, [pending])

  function settle(ok: boolean) {
    pending?.resolve(ok)
    ref.current?.close()
    setPending(null)
  }

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      {pending && (
        <dialog
          ref={ref}
          className="dialog"
          aria-labelledby="confirm-title"
          onCancel={(e) => {
            e.preventDefault()
            settle(false)
          }}
          onClick={(e) => {
            if (e.target === e.currentTarget) settle(false)
          }}
        >
          <div className="dialog-body">
            <h2 id="confirm-title">{pending.title}</h2>
            {pending.message && <p>{pending.message}</p>}
            <div className="dialog-actions">
              <button type="button" className="btn" onClick={() => settle(false)}>
                Cancel
              </button>
              <button
                type="button"
                className={`btn ${pending.danger ? 'danger' : 'primary'}`}
                autoFocus
                onClick={() => settle(true)}
              >
                {pending.confirmLabel ?? 'Confirm'}
              </button>
            </div>
          </div>
        </dialog>
      )}
    </ConfirmContext.Provider>
  )
}
