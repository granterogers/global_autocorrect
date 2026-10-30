// CKEditor 5, the editor behind Microsoft Teams' message box. Classic build with typing only: no toolbar items, no
// autoformat or text transformation, so what the page shows is only what was typed and what the app changed.
import { ClassicEditor, Essentials, Paragraph } from 'ckeditor5';
import 'ckeditor5/ckeditor5.css';

ClassicEditor.create(document.getElementById('ed'), {
  licenseKey: 'GPL',
  plugins: [Essentials, Paragraph],
  toolbar: [],
}).then((editor) => {
  window.editor = editor;
  editor.editing.view.focus();
  // Read back from the editor's model, not the DOM (11.3.b): paragraphs joined by "\n", soft breaks as "\n".
  window.getText = () => Array.from(editor.model.document.getRoot().getChildren())
    .map((block) => Array.from(block.getChildren())
      .map((n) => (n.is('$text') ? n.data : n.is('element', 'softBreak') ? '\n' : ''))
      .join(''))
    .join('\n');
});
