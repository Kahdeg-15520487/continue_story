export interface BookSummary {
  id: number;
  slug: string;
  title: string;
  author: string | null;
  year: number | null;
  status: string;
  updatedAt: string;
}

export interface BookDetail {
  id: number;
  slug: string;
  title: string;
  author: string | null;
  year: number | null;
  sourceFile: string | null;
  status: string;
  errorMessage: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface BookContent {
  slug: string;
  content: string;
}

export interface CreateBookRequest {
  title: string;
  author?: string;
  year?: number;
  sourceFile?: string;
}

export interface ChatRequest {
  bookSlug: string;
  message: string;
}

export interface LoreFiles {
  files: string[];
}

export interface LoreContent {
  file: string;
  content: string;
}

export interface UploadResult {
  slug: string;
  sourceFile: string;
  size: number;
  status: string;
  jobId: string;
}

export interface ConversionStatus {
  status: string;
  sourceFile: string | null;
  errorMessage: string | null;
  updatedAt: string;
  hangfire: {
    enqueued: number;
    processing: number;
    succeeded: number;
    failed: number;
  };
}
