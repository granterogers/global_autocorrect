// Quill 2 (Snow theme, no toolbar): the editor behind many web apps' comment and message boxes. Formats off, so the page
// shows only what was typed and what the app changed.
import Quill from 'quill';
import 'quill/dist/quill.snow.css';

const quill = new Quill('#ed', { theme: 'snow', modules: { toolbar: false }, formats: [] });
quill.root.setAttribute('spellcheck', 'false');
quill.focus();
window.quill = quill;
// Read back from Quill's document model, not the DOM (11.3.b). getText() always ends with the document's own "\n".
window.getText = () => quill.getText();
