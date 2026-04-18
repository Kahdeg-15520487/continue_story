<script lang="ts">
  import { api } from '$lib/api';

  let { slug, onUploaded }: {
    slug: string;
    onUploaded: (result: { sourceFile: string; status: string }) => void;
  } = $props();

  let dragging = $state(false);
  let uploading = $state(false);
  let progress = $state(0);
  let uploadError = $state('');
  let selectedFile: File | null = $state(null);

  const ACCEPTED = '.epub,.pdf,.docx,.doc,.txt,.html,.htm,.pptx,.xlsx,.xls,.csv,.ipynb,.md';

  function handleFile(file: File) {
    const ext = file.name.lastIndexOf('.') >= 0
      ? file.name.slice(file.name.lastIndexOf('.')).toLowerCase()
      : '';
    const allowed = ACCEPTED.split(',').map(e => e.trim());
    if (!allowed.includes(ext)) {
      uploadError = `Unsupported file type: ${ext || 'none'}. Accepted: ${ACCEPTED}`;
      return;
    }
    selectedFile = file;
    uploadError = '';
  }

  function handleDrop(e: DragEvent) {
    e.preventDefault();
    dragging = false;
    const file = e.dataTransfer?.files[0];
    if (file) handleFile(file);
  }

  function handleDragOver(e: DragEvent) {
    e.preventDefault();
    dragging = true;
  }

  function handleDragLeave() {
    dragging = false;
  }

  function handleInputChange(e: Event) {
    const target = e.target as HTMLInputElement;
    const file = target.files?.[0];
    if (file) handleFile(file);
  }

  async function upload() {
    if (!selectedFile) return;

    uploading = true;
    progress = 0;
    uploadError = '';

    try {
      const result = await api.upload(slug, selectedFile, (pct) => {
        progress = pct;
      });
      onUploaded(result);
    } catch (err: any) {
      uploadError = err.message || 'Upload failed';
      uploading = false;
    }
  }

  function clear() {
    selectedFile = null;
    uploadError = '';
    progress = 0;
  }
</script>

<div class="upload-zone">
  {#if !uploading}
    <!-- Drop zone / file selector -->
    <div
      class="drop-area"
      class:dragging
      ondrop={handleDrop}
      ondragover={handleDragOver}
      ondragleave={handleDragLeave}
      role="button"
      tabindex="0"
    >
      {#if selectedFile}
        <div class="file-preview">
          <span class="file-icon">📄</span>
          <span class="file-name">{selectedFile.name}</span>
          <span class="file-size">({(selectedFile.size / 1024 / 1024).toFixed(1)} MB)</span>
          <button class="btn-clear" onclick={clear} title="Remove file">✕</button>
        </div>
        <button class="btn-upload" onclick={upload}>
          Upload & Convert
        </button>
      {:else}
        <p class="drop-hint">Drag & drop a file here, or click to browse</p>
        <p class="drop-formats">EPUB, PDF, DOCX, TXT, HTML, and more</p>
      {/if}

      {#if !selectedFile}
        <input
          type="file"
          accept={ACCEPTED}
          onchange={handleInputChange}
          class="file-input"
        />
      {/if}
    </div>
  {:else}
    <!-- Upload progress -->
    <div class="progress-area">
      <div class="progress-label">
        Uploading... {progress}%
      </div>
      <div class="progress-bar">
        <div class="progress-fill" style="width: {progress}%"></div>
      </div>
    </div>
  {/if}

  {#if uploadError}
    <div class="upload-error">{uploadError}</div>
  {/if}
</div>

<style>
  .upload-zone {
    width: 100%;
    max-width: 600px;
    margin: 0 auto;
  }

  .drop-area {
    border: 2px dashed var(--border);
    border-radius: 12px;
    padding: 40px 24px;
    text-align: center;
    cursor: pointer;
    transition: border-color 0.2s, background-color 0.2s;
    position: relative;
  }

  .drop-area:hover,
  .drop-area.dragging {
    border-color: var(--accent);
    background: rgba(99, 102, 241, 0.05);
  }

  .drop-hint {
    color: var(--text-primary);
    font-size: 15px;
    margin-bottom: 8px;
  }

  .drop-formats {
    color: var(--text-secondary);
    font-size: 12px;
  }

  .file-input {
    position: absolute;
    inset: 0;
    opacity: 0;
    cursor: pointer;
  }

  .file-preview {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    margin-bottom: 16px;
    flex-wrap: wrap;
  }

  .file-icon {
    font-size: 20px;
  }

  .file-name {
    font-weight: 600;
    font-size: 14px;
    color: var(--text-primary);
    word-break: break-all;
  }

  .file-size {
    color: var(--text-secondary);
    font-size: 12px;
  }

  .btn-clear {
    background: none;
    border: none;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 14px;
    padding: 2px 6px;
    border-radius: 4px;
  }

  .btn-clear:hover {
    color: #f97583;
  }

  .btn-upload {
    background: var(--accent);
    color: white;
    border: none;
    padding: 10px 24px;
    border-radius: 8px;
    font-size: 14px;
    font-weight: 600;
    cursor: pointer;
    transition: opacity 0.2s;
  }

  .btn-upload:hover {
    opacity: 0.9;
  }

  .progress-area {
    padding: 24px;
  }

  .progress-label {
    font-size: 14px;
    color: var(--text-primary);
    margin-bottom: 8px;
  }

  .progress-bar {
    height: 8px;
    background: var(--bg-tertiary);
    border-radius: 4px;
    overflow: hidden;
  }

  .progress-fill {
    height: 100%;
    background: var(--accent);
    border-radius: 4px;
    transition: width 0.3s ease;
  }

  .upload-error {
    margin-top: 12px;
    padding: 8px 12px;
    background: #3d1f1f;
    color: #f97583;
    border-radius: 6px;
    font-size: 12px;
  }
</style>
