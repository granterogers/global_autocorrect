// D113: stand-ins for the message boxes the user types into most, built on ProseMirror like the real ones.
//   data-mode="intercom": Enter starts a new paragraph; "@" + name + space becomes an uneditable mention block (UIA
//                         reads it as U+FFFC, as in Intercom); a heavy page (busy work on every change).
//   data-mode="chat":     Claude / ChatGPT: Enter sends the message and clears the box, Shift+Enter is a new line.
// data-heavy = milliseconds of main-thread work per change, as a large web app spends.
// data-signature = lines ("|" between) already in the box below the caret, as Intercom's email reply has (D122): every
//                  fix on an earlier line then has text after the caret, so Ctrl+End can't bring the caret back.
import { EditorState, Plugin, TextSelection } from 'prosemirror-state';
import { EditorView } from 'prosemirror-view';
import { Schema } from 'prosemirror-model';
import { schema as basic } from 'prosemirror-schema-basic';
import { keymap } from 'prosemirror-keymap';
import { baseKeymap, splitBlock } from 'prosemirror-commands';
import { history, undo, redo } from 'prosemirror-history';

const mode = document.body.dataset.mode || 'intercom';
const heavy = Number(document.body.dataset.heavy || 0);
const signature = (document.body.dataset.signature || '').split('|').filter(s => s.length > 0);

const schema = new Schema({
  nodes: basic.spec.nodes.addToEnd('mention', {
    inline: true, group: 'inline', atom: true, selectable: false,
    attrs: { name: { default: '' } },
    toDOM: n => ['span', { class: 'mention', contenteditable: 'false', 'data-name': n.attrs.name }, '@' + n.attrs.name],
    parseDOM: [{ tag: 'span.mention', getAttrs: d => ({ name: d.dataset.name }) }],
  }),
  marks: basic.spec.marks,
});

window.sent = [];

function send(state, dispatch) {
  const text = state.doc.textBetween(0, state.doc.content.size, '\n', '￼');
  if (!text.trim()) return true;
  window.sent.push(text);
  const log = document.getElementById('sent');
  if (log) { const li = document.createElement('li'); li.textContent = text; log.appendChild(li); }
  if (dispatch) {
    const fresh = EditorState.create({ schema, plugins: state.plugins });
    queueMicrotask(() => view.updateState(fresh));   // the box clears, as Claude's and ChatGPT's do
  }
  return true;
}

// "@name " → a mention block (Intercom).
const mentions = new Plugin({
  appendTransaction(trs, _old, state) {
    if (mode !== 'intercom' || !trs.some(t => t.docChanged)) return null;
    const { $from } = state.selection;
    const before = $from.parent.textBetween(0, $from.parentOffset, null, '￼');
    const m = /@([A-Za-z]+) $/.exec(before);
    if (!m) return null;
    const start = $from.pos - m[0].length, end = $from.pos - 1;
    return state.tr.replaceWith(start, end, schema.nodes.mention.create({ name: m[1] }));
  },
});

// Busy work on every change: Intercom spends tens of milliseconds per key on its own bookkeeping.
const busy = new Plugin({ view: () => ({ update: () => { const until = performance.now() + heavy; while (performance.now() < until) { /* busy */ } } }) });

const lineBreak = (s, d) => { if (d) d(s.tr.replaceSelectionWith(schema.nodes.hard_break.create()).scrollIntoView()); return true; };
const keys = mode === 'chat' ? { Enter: send, 'Shift-Enter': splitBlock } : { Enter: splitBlock, 'Shift-Enter': lineBreak };

const para = t => schema.nodes.paragraph.create(null, t ? schema.text(t) : null);
const startDoc = signature.length ? schema.nodes.doc.create(null, [para(''), para(''), ...signature.map(para)]) : undefined;
const view = new EditorView(document.getElementById('ed'), {
  state: EditorState.create({
    schema,
    doc: startDoc,
    selection: startDoc ? TextSelection.create(startDoc, 1) : undefined,
    plugins: [history(), keymap({ 'Mod-z': undo, 'Mod-y': redo }), keymap(keys), keymap(baseKeymap), mentions, ...(heavy > 0 ? [busy] : [])],
  }),
});
view.focus();
window.view = view;

// What the test reads: the sent messages, then what is still in the box, one line each.
window.caretToTop = () => { view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 1))); view.focus(); };

// With a signature: the text above it, and a line that fails the test if the signature was touched.
const boxText = () => {
  const all = view.state.doc.textBetween(0, view.state.doc.content.size, '\n', '￼');
  if (!signature.length) return all;
  const sig = '\n\n' + signature.join('\n');
  return all.endsWith(sig) ? all.slice(0, -sig.length) : all + '\nSIGNATURE DAMAGED';
};
window.getText = () => [...window.sent, boxText()].filter(s => s.length > 0).join('\n');
