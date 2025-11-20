import React, { useMemo, useState } from 'react'

const DEFAULT_API = 'http://localhost:5174'

export default function App() {
  const [selectedFiles, setSelectedFiles] = useState([])
  const [isUploading, setIsUploading] = useState(false)
  const [isDownloading, setIsDownloading] = useState(false)
  const [results, setResults] = useState([])
  const [error, setError] = useState(null)
  const [mode, setMode] = useState('lossless') // 'lossless' | 'auto'

  const apiBaseUrl = useMemo(() => {
    return import.meta.env.VITE_API_BASE_URL || DEFAULT_API
  }, [])

  function onFilesChanged(e) {
    const files = Array.from(e.target.files || [])
    setSelectedFiles(files)
  }

  async function onSubmit(e) {
    e.preventDefault()
    setIsUploading(true)
    setError(null)
    setResults([])

    try {
      const form = new FormData()
      for (const f of selectedFiles) {
        form.append('files', f, f.name)
      }

      const res = await fetch(`${apiBaseUrl}/api/convert?mode=${encodeURIComponent(mode)}`, {
        method: 'POST',
        body: form
      })

      if (!res.ok) {
        const txt = await res.text()
        throw new Error(txt || `Request failed with status ${res.status}`)
      }

      const data = await res.json()
      setResults(data.files || [])
    } catch (err) {
      setError(err.message || String(err))
    } finally {
      setIsUploading(false)
    }
  }

  async function onDownloadZip(e) {
    e.preventDefault()
    if (selectedFiles.length === 0) return
    setIsDownloading(true)
    setError(null)
    try {
      const form = new FormData()
      for (const f of selectedFiles) {
        form.append('files', f, f.name)
      }
      const res = await fetch(`${apiBaseUrl}/api/convert/zip?mode=${encodeURIComponent(mode)}`, {
        method: 'POST',
        body: form
      })
      if (!res.ok) {
        const txt = await res.text()
        throw new Error(txt || `Request failed with status ${res.status}`)
      }
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = 'converted.zip'
      document.body.appendChild(a)
      a.click()
      a.remove()
      URL.revokeObjectURL(url)
    } catch (err) {
      setError(err.message || String(err))
    } finally {
      setIsDownloading(false)
    }
  }

  return (
    <div className="container">
      <h1>PDF/Image → Images</h1>
      <form onSubmit={onSubmit}>
        <input
          type="file"
          multiple
          accept=".pdf,image/*"
          onChange={onFilesChanged}
        />
        <select value={mode} onChange={(e) => setMode(e.target.value)}>
          <option value="lossless">Lossless (no quality loss)</option>
          <option value="auto">Auto (smaller, visually safe)</option>
        </select>
        <button type="submit" disabled={isUploading || selectedFiles.length === 0}>
          {isUploading ? 'Processing…' : 'Upload & Convert'}
        </button>
        <button onClick={onDownloadZip} disabled={isDownloading || selectedFiles.length === 0}>
          {isDownloading ? 'Preparing ZIP…' : 'Download ZIP'}
        </button>
      </form>

      {selectedFiles.length > 0 && (
        <div className="hint">
          {selectedFiles.length} file(s) selected
        </div>
      )}

      {error && <div className="error">Error: {error}</div>}

      <div className="results">
        {results.map((file, idx) => (
          <div key={idx} className="file-block">
            <div className="file-title">{file.originalFileName}</div>
            <div className="pages-grid">
              {file.pages.map((p) => (
                <div key={p.pageNumber} className="page-card">
                  <div className="page-meta">Page {p.pageNumber} • {(p.sizeBytes / 1024).toFixed(1)} KB</div>
                  <img src={p.dataUrl} alt={`Page ${p.pageNumber}`} />
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}


