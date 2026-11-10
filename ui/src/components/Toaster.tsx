import { CircleAlert, CircleCheck, X } from 'lucide-react'
import { useAppData } from '../AppData'

export function Toaster() {
  const { toast, setToast } = useAppData()
  return (
    <div className="toaster" role="status" aria-live="polite">
      {toast && (
        <div className={`toast ${toast.kind}`}>
          {toast.kind === 'error' ? <CircleAlert size={18} /> : <CircleCheck size={18} />}
          <span className="msg">{toast.message}</span>
          <button
            type="button"
            className="copy-btn"
            aria-label="Dismiss"
            onClick={() => setToast(null)}
          >
            <X size={14} />
          </button>
        </div>
      )}
    </div>
  )
}
