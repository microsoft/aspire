import { Fragment } from "react";

interface TextFragment {
  text: string;
  match: boolean;
}

function getFragments(text: string, term: string): TextFragment[] {
  if (text.length === 0) {
    return [];
  }
  if (term.length === 0) {
    return [{ text, match: false }];
  }

  const fragments: TextFragment[] = [];
  const normalizedText = text.toLocaleLowerCase();
  const normalizedTerm = term.toLocaleLowerCase();
  let start = 0;
  let matchIndex = normalizedText.indexOf(normalizedTerm, start);
  while (matchIndex >= 0) {
    if (matchIndex > start) {
      fragments.push({ text: text.slice(start, matchIndex), match: false });
    }
    fragments.push({ text: text.slice(matchIndex, matchIndex + term.length), match: true });
    start = matchIndex + term.length;
    matchIndex = normalizedText.indexOf(normalizedTerm, start);
  }
  if (start < text.length) {
    fragments.push({ text: text.slice(start), match: false });
  }
  return fragments;
}

export function Highlighter({ text, highlightedText }: { text: string; highlightedText?: string }) {
  return (
    <>
      {getFragments(text, highlightedText ?? "").map((fragment, index) =>
        fragment.match ? (
          <mark className="deck-mark" key={index}>
            {fragment.text}
          </mark>
        ) : (
          <Fragment key={index}>{fragment.text}</Fragment>
        ),
      )}
    </>
  );
}
