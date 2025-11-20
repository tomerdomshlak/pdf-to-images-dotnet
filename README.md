## PDF/Image to Images (React + ASP.NET Core)

Two local apps:
- **Backend**: ASP.NET Core Web API (.NET 7) converts PDFs to images and compresses images using Magick.NET.
- **Frontend**: React (Vite) uploads multiple files and previews returned images.

The backend returns images as base64 data URLs and the frontend displays them as page previews.

### Features
- Upload multiple PDFs and/or images
- PDFs rendered page-by-page at 300 DPI for crisp invoice-like documents
- Images compressed to WebP with tuned settings to preserve quality while reducing size
- Simple previews in the browser

### Prerequisites
- .NET 7 SDK (or newer)
- Node.js 18+ and npm
- Ghostscript (required for PDF input with Magick.NET)
  - macOS: `brew install ghostscript`
  - Windows (admin PowerShell): `choco install ghostscript`

After installing Ghostscript, ensure the `gs` binary is on your PATH (restart terminal if needed).

### Run Backend (ASP.NET Core)
1. Install dependencies and run:
   - macOS / Linux:
     ```
     cd /Users/tomerdomshlak/code-kings/workspaces/internal/pdf-to-images-dotnet/backend
     dotnet restore
     dotnet run
     ```
   - Windows:
     ```
     cd \Users\tomerdomshlak\code-kings\workspaces\internal\pdf-to-images-dotnet\backend
     dotnet restore
     dotnet run
     ```
2. Service defaults to `http://localhost:5174` (Swagger at `/swagger`).

### Run Frontend (React)
1. Install and start dev server:
   ```
   cd /Users/tomerdomshlak/code-kings/workspaces/internal/pdf-to-images-dotnet/frontend
   npm install
   npm run dev
   ```
2. Open the printed local URL (default `http://localhost:5173`).
3. By default, the frontend calls the backend at `http://localhost:5174`. To change:
   - Create `frontend/.env` with:
     ```
     VITE_API_BASE_URL=http://localhost:5174
     ```

### API
POST `POST /api/convert` (multipart/form-data)
- Field: `files` (repeatable) — PDF or image files
- Response:
  ```json
  {
    "files": [
      {
        "originalFileName": "sample.pdf",
        "pages": [
          {
            "pageNumber": 1,
            "mimeType": "image/webp",
            "dataUrl": "data:image/webp;base64,...",
            "width": 2480,
            "height": 3508,
            "sizeBytes": 123456
          }
        ]
      }
    ]
  }
  ```

### Notes
- Quality focus: PDFs are rasterized at 300 DPI for sharp text. WebP encoding uses method=6 and quality=85 for a balance of quality and size similar in spirit to TinyPNG, but fully local.
- Multi-frame images (e.g., TIFF) are handled as multiple pages.
- CORS is open for local development.

### Troubleshooting
- If PDFs fail to load: verify Ghostscript is installed and on PATH.
- If the backend port differs: update `frontend/.env` with `VITE_API_BASE_URL`.
- For very large files, you can increase the request limit in `ConvertController` via `[RequestSizeLimit]`.
