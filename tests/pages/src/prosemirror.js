import { EditorState } from 'prosemirror-state';
import { EditorView } from 'prosemirror-view';
import { schema } from 'prosemirror-schema-basic';
import { exampleSetup } from 'prosemirror-example-setup';

const view = new EditorView(document.getElementById('ed'), {
  state: EditorState.create({ schema, plugins: exampleSetup({ schema, menuBar: false }) }),
});
view.focus();

// Read back from editor state, not the DOM (11.3.b).
window.getText = () => view.state.doc.textContent;
