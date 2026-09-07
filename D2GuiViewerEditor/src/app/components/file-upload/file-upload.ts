import { Component, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FileUpload as FileUploadService, ZipUploadResponse } from '../../services/file-upload';

@Component({
  selector: 'd2-file-upload',
  imports: [CommonModule],
  templateUrl: './file-upload.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './file-upload.scss',
})
export class FileUploadComponent {
  selectedFile: File | null = null;
  uploadResponse: ZipUploadResponse | null = null;
  isUploading = false;
  errorMessage: string | null = null;

  constructor(private fileUploadService: FileUploadService) {}

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.selectedFile = input.files[0];
      this.uploadResponse = null;
      this.errorMessage = null;
    }
  }

  uploadFile(): void {
    if (!this.selectedFile) {
      this.errorMessage = 'Proszę wybrać plik ZIP';
      return;
    }

    if (!this.selectedFile.name.toLowerCase().endsWith('.zip')) {
      this.errorMessage = 'Wybrany plik musi być archiwum ZIP';
      return;
    }

    this.isUploading = true;
    this.errorMessage = null;

    this.fileUploadService.uploadZipFile(this.selectedFile).subscribe({
      next: (response) => {
        if (response.success) {
          this.uploadResponse = response;
        } else {
          this.errorMessage = response.message || 'Wystąpił błąd podczas przetwarzania pliku';
          if (response.errorDetails) {
            console.error('Error details:', response.errorDetails);
          }
        }
        this.isUploading = false;
      },
      error: (error) => {
        const errorResponse = error.error as ZipUploadResponse;
        this.errorMessage = errorResponse?.message || error.message || 'Wystąpił błąd podczas przesyłania pliku';
        this.isUploading = false;
        console.error('Upload error:', error);
      }
    });
  }

  formatFileSize(bytes: number): string {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return Math.round(bytes / Math.pow(k, i) * 100) / 100 + ' ' + sizes[i];
  }
}
