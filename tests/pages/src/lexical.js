import { createEditor, $getRoot } from 'lexical';
import { registerRichText, HeadingNode, QuoteNode } from '@lexical/rich-text';

const editor = createEditor({
  namespace: 'ga-test',
  nodes: [HeadingNode, QuoteNode],
  onError: (e) => { throw e; },
});
const root = document.getElementById('ed');
editor.setRootElement(root);
// Without the rich-text plugin Lexical ignores keystrokes (12.5).
registerRichText(editor);
root.focus();

window.getText = () => editor.getEditorState().read(() => $getRoot().getTextContent());
