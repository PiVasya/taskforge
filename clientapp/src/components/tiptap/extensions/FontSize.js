import { Extension } from "@tiptap/core";

// Stores font size on TextStyle via inline style: font-size: XXpx
export const FontSize = Extension.create({
  name: "fontSize",

  addOptions() {
    return { types: ["textStyle"] };
  },

  addGlobalAttributes() {
    return [
      {
        types: this.options.types,
        attributes: {
          fontSize: {
            default: null,
            parseHTML: (element) => {
              const v = element.style?.fontSize;
              return v ? String(v).replace(/["']/g, "").trim() : null;
            },
            renderHTML: (attributes) => {
              if (!attributes.fontSize) return {};
              const raw = String(attributes.fontSize).trim();
              const size = /^\d+$/.test(raw) ? `${raw}px` : raw;
              return { style: `font-size: ${size}` };
            },
          },
        },
      },
    ];
  },

  addCommands() {
    return {
      setFontSize:
        (fontSize) =>
        ({ chain }) =>
          chain().setMark("textStyle", { fontSize }).run(),

      unsetFontSize:
        () =>
        ({ chain }) =>
          chain()
            .setMark("textStyle", { fontSize: null })
            .removeEmptyTextStyle()
            .run(),
    };
  },
});
