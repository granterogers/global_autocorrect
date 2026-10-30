// Slate (React): plain paragraphs, no marks or shortcuts, so the page shows only what was typed and what the app
// changed. Slate keeps its own document model and re-renders the DOM from it on every change.
import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { createEditor, Node } from 'slate';
import { Slate, Editable, withReact } from 'slate-react';

const initialValue = [{ type: 'paragraph', children: [{ text: '' }] }];

function App() {
  const [editor] = useState(() => withReact(createEditor()));
  // Read back from Slate's model, not the DOM (11.3.b): paragraphs joined by "\n".
  window.getText = () => editor.children.map((n) => Node.string(n)).join('\n');
  window.editor = editor;
  return (
    <Slate editor={editor} initialValue={initialValue}>
      <Editable id="ed-editable" spellCheck={false} autoFocus
        style={{ minHeight: 160, padding: 8, border: '1px solid #888', outline: 'none' }} />
    </Slate>
  );
}

createRoot(document.getElementById('ed')).render(<App />);
