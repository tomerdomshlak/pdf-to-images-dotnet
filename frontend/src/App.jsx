import React, { useMemo, useState } from 'react'

const DEFAULT_API = 'http://localhost:5174'

export default function App() {
  const [selectedFiles, setSelectedFiles] = useState([])
  const [isUploading, setIsUploading] = useState(false)
  const [isDownloading, setIsDownloading] = useState(false)
  const [results, setResults] = useState([])
  const [error, setError] = useState(null)

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

      const res = await fetch(`${apiBaseUrl}/api/convert`, {
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
      const res = await fetch(`${apiBaseUrl}/api/convert/zip`, {
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
                <PageCard key={p.pageNumber} page={p} />
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}

function PageCard({ page }) {
  const [selectedId, setSelectedId] = useState(null)
  const items = Array.isArray(page.ocr?.items) ? page.ocr.items : []
  const normalizeLevel = (lvl) => {
    if (typeof lvl === 'string') return lvl
    switch (lvl) {
      case 2: return 'Block'
      case 3: return 'Paragraph'
      case 4: return 'Line'
      case 5: return 'Word'
      default: return String(lvl ?? '')
    }
  }
  const levelIsWord = (lvl) => normalizeLevel(lvl) === 'Word'
  const levelIsLine = (lvl) => normalizeLevel(lvl) === 'Line'
  const levelIsParagraph = (lvl) => normalizeLevel(lvl) === 'Paragraph'
  const textItems = items.filter(it => (it?.text || '').trim().length > 0)
  const wordItems = textItems.filter(it => levelIsWord(it.level))
  const lineItems = textItems.filter(it => levelIsLine(it.level))
  const paraItems = textItems.filter(it => levelIsParagraph(it.level))
  const displayItems = wordItems.length ? wordItems : (lineItems.length ? lineItems : (paraItems.length ? paraItems : textItems))
  const selectedItem = items.find(i => i.id === selectedId) || null
  const percentRect = selectedItem
    ? {
        left: `${(selectedItem.box.left / page.width) * 100}%`,
        top: `${(selectedItem.box.top / page.height) * 100}%`,
        width: `${(selectedItem.box.width / page.width) * 100}%`,
        height: `${(selectedItem.box.height / page.height) * 100}%`,
      }
    : null

  return (
    <div className="page-card">
      <div className="page-meta">Page {page.pageNumber} • {(page.sizeBytes / 1024).toFixed(1)} KB</div>
      <div className="page-content">
        <div className="page-viewport">
          <img src={page.dataUrl} alt={`Page ${page.pageNumber}`} />
          {percentRect && (
            <div className="page-highlight" style={percentRect} />
          )}
        </div>
        <div className="page-ocr">
          <div className="page-ocr-title">OCR</div>
          {page.ocr ? (
            <>
              <div className="page-ocr-list">
                {displayItems
                  .slice(0, 500)
                  .map(it => (
                    <button
                      key={it.id}
                      className={`ocr-item ${selectedId === it.id ? 'active' : ''}`}
                      title={`Conf: ${typeof it.confidence === 'number' ? it.confidence.toFixed(2) : ''}`}
                      onClick={() => setSelectedId(it.id)}
                    >
                      {normalizeLevel(it.level)}: {it.text || '(blank)'}
                    </button>
                  ))}
              </div>
              <details className="page-ocr-raw">
                <summary>Raw JSON</summary>
                <pre>{JSON.stringify(page.ocr, null, 2)}</pre>
              </details>
            </>
          ) : (
            <div className="page-ocr-empty">No OCR</div>
          )}
        </div>
      </div>
    </div>
  )
}


