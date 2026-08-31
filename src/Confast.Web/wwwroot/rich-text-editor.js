window.confastRichText = (() => {
  const editors = new WeakMap();
  const toolbar = [
    [{ font: [] }, { size: [] }],
    ['bold', 'italic', 'underline'], [{ color: [] }],
    [{ list: 'ordered' }, { list: 'bullet' }], [{ align: [] }],
    ['link'], ['undo', 'redo']
  ];

  function create(host, dotnet, value) {
    const quill = new Quill(host, { theme: 'snow', modules: { toolbar } });
    quill.root.innerHTML = value || '';
    quill.root.addEventListener('paste', event => {
      const image = Array.from(event.clipboardData?.files || [])
        .find(file => /^(image\/(png|jpe?g|gif|webp))$/i.test(file.type));
      if (!image || image.size > 2 * 1024 * 1024) return;
      event.preventDefault();
      const reader = new FileReader();
      reader.onload = () => {
        const range = quill.getSelection(true);
        quill.insertEmbed(range ? range.index : quill.getLength(), 'image', reader.result, 'user');
      };
      reader.readAsDataURL(image);
    });
    // Quill's Snow theme inserts an array-configured toolbar immediately before
    // the editor container, rather than inside it. Keep this lookup tolerant of
    // both that shape and a future/custom toolbar container.
    const controls = host.querySelector('.ql-toolbar')
      || host.previousElementSibling?.matches('.ql-toolbar') && host.previousElementSibling;
    const undo = controls?.querySelector('.ql-undo');
    const redo = controls?.querySelector('.ql-redo');
    undo?.addEventListener('click', () => quill.history.undo());
    redo?.addEventListener('click', () => quill.history.redo());
    quill.on('text-change', () => dotnet.invokeMethodAsync('Changed', quill.root.innerHTML));
    editors.set(host, quill);
  }

  function setHtml(host, value) {
    const quill = editors.get(host);
    if (quill && quill.root.innerHTML !== value) quill.root.innerHTML = value || '';
  }

  function dispose(host) { editors.delete(host); }
  return { create, setHtml, dispose };
})();
