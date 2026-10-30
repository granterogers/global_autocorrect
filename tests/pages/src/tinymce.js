// TinyMCE 8, self-hosted from npm (GPL licence key, no Tiny Cloud, no API key). Classic (iframe) mode as most CMSs use
// it; no plugins, toolbar or text patterns, so the page shows only what was typed and what the app changed. The skin
// and content CSS are bundled as resources (skin.js / content.js), so nothing is fetched at run time.
import tinymce from 'tinymce';
import 'tinymce/models/dom/model';
import 'tinymce/themes/silver';
import 'tinymce/icons/default';
import 'tinymce/skins/ui/oxide/skin.js';
import 'tinymce/skins/ui/oxide/content.js';
import 'tinymce/skins/content/default/content.js';

tinymce.init({
  selector: '#ed',
  license_key: 'gpl',
  skin_url: 'default',
  content_css: 'default',
  menubar: false,
  toolbar: false,
  statusbar: false,
  promotion: false,
  branding: false,
  text_patterns: false,
  browser_spellcheck: false,
  width: 720,
  height: 220,
}).then(([editor]) => {
  window.editor = editor;
  editor.focus();
  // Read back from the editor, not the DOM (11.3.b): paragraphs as lines.
  window.getText = () => editor.getContent({ format: 'text' });
});
