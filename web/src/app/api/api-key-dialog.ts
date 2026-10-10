import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

@Component({
  selector: 'app-api-key-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Enter the API key</h2>
    <form>
      <mat-dialog-content>
        <p>This dashboard's data needs a key. It is saved in this browser only.</p>
        <mat-form-field appearance="outline" style="width: 100%">
          <mat-label>API key</mat-label>
          <input
            matInput
            type="password"
            name="key"
            [(ngModel)]="key"
            autocomplete="off"
            cdkFocusInitial
          />
        </mat-form-field>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" [mat-dialog-close]="null">Cancel</button>
        <button mat-flat-button type="submit" [mat-dialog-close]="key()" [disabled]="!key()">
          Save key
        </button>
      </mat-dialog-actions>
    </form>
  `,
})
export class ApiKeyDialog {
  protected readonly key = signal('');
}
