import { createElement, type ReactNode } from 'react';

/**
 * A safe Markdown preview: a small subset rendered as React nodes, never as HTML.
 *
 * <b>Nothing here executes.</b> The source is unapproved text that may contain a script tag, a
 * prompt or a link, and the page asking whether to believe it must not be the page that runs
 * it. So there is no `dangerouslySetInnerHTML` anywhere in this module: every run of source
 * characters becomes a React text node, which the renderer escapes, and the only elements ever
 * created are the fixed set below with no attributes except a link `href` that passed the
 * protocol check.
 *
 * <b>Links carry http/https only.</b> A `javascript:`, `data:` or protocol-relative destination
 * is rendered as its own text with no anchor, so an unsafe URL is visible as characters rather
 * than reachable as navigation. A raw HTML tag in the source is likewise visible as the
 * characters `<`, `b`, `>` - there is no tag parsing here at all, which is what makes a tag
 * impossible to smuggle through.
 *
 * The subset is headings (`#` to `###`), unordered lists (`- `), fenced code blocks, inline
 * code, bold, links and footnote references (`[^id]`, shown as superscript text with no
 * destination). Everything else is a paragraph of literal text.
 */

const ALLOWED_PROTOCOLS = ['http:', 'https:'];

/** Renders Markdown source as inert React nodes. */
export function renderSafeMarkdown(source: string): ReactNode {
  const blocks = splitBlocks(source);
  const nodes: ReactNode[] = [];

  blocks.forEach((block, index) => {
    nodes.push(renderBlock(block, index));
  });

  return nodes;
}

interface TextBlock {
  readonly kind: 'text';
  readonly lines: readonly string[];
}

interface CodeBlock {
  readonly kind: 'code';
  readonly lines: readonly string[];
}

type Block = TextBlock | CodeBlock;

function splitBlocks(source: string): readonly Block[] {
  const lines = source.split('\n');
  const blocks: Block[] = [];
  let current: string[] = [];
  let code: string[] | null = null;

  const flush = () => {
    if (current.length > 0) {
      blocks.push({ kind: 'text', lines: current });
      current = [];
    }
  };

  for (const line of lines) {
    if (code !== null) {
      if (line.trimStart().startsWith('```')) {
        blocks.push({ kind: 'code', lines: code });
        code = null;
      } else {
        code.push(line);
      }
      continue;
    }

    if (line.trimStart().startsWith('```')) {
      flush();
      code = [];
      continue;
    }

    if (line.trim() === '') {
      flush();
      continue;
    }

    current.push(line);
  }

  flush();

  if (code !== null) {
    blocks.push({ kind: 'code', lines: code });
  }

  return blocks;
}

function renderBlock(block: Block, index: number): ReactNode {
  if (block.kind === 'code') {
    return createElement(
      'pre',
      { key: index, className: 'review__code', 'data-testid': 'review-preview-code' },
      block.lines.join('\n'),
    );
  }

  const first = block.lines[0] ?? '';
  const heading = /^#{1,3} /.exec(first);

  if (heading !== null) {
    const level = heading[0].trim().length;
    const text = first.slice(level + 1);
    const tag = level === 1 ? 'h4' : level === 2 ? 'h5' : 'h6';

    return createElement(
      tag,
      { key: index, className: 'review__heading', 'data-testid': 'review-preview-heading' },
      renderInline(text, `${index}-h`),
    );
  }

  if (block.lines.every((line) => /^\s*-\s+/.test(line))) {
    return createElement(
      'ul',
      { key: index, className: 'review__list', 'data-testid': 'review-preview-list' },
      block.lines.map((line, lineIndex) =>
        createElement(
          'li',
          { key: lineIndex },
          renderInline(line.replace(/^\s*-\s+/, ''), `${index}-${lineIndex}`),
        ),
      ),
    );
  }

  return createElement(
    'p',
    { key: index, className: 'review__paragraph', 'data-testid': 'review-preview-paragraph' },
    block.lines.map((line, lineIndex) => [
      lineIndex === 0 ? null : createElement('br', { key: `${lineIndex}-br` }),
      createElement('span', { key: lineIndex }, renderInline(line, `${index}-${lineIndex}`)),
    ]),
  );
}

/**
 * Renders one line's inline runs. Code spans are atomic - no bold or link is recognized inside
 * them - and footnote references are superscript text with no destination, because a citation is
 * something a reader follows back to the claim list, not something this preview navigates to.
 */
function renderInline(text: string, keyPrefix: string): ReactNode[] {
  const nodes: ReactNode[] = [];
  let rest = text;
  let key = 0;

  const pushText = (value: string) => {
    if (value !== '') {
      nodes.push(value);
    }
  };

  while (rest !== '') {
    const code = /^`([^`\n]+)`/.exec(rest);
    if (code !== null) {
      nodes.push(createElement('code', { key: `${keyPrefix}-${key++}` }, code[1]));
      rest = rest.slice(code[0].length);
      continue;
    }

    const bold = /^\*\*([^*]+)\*\*/.exec(rest);
    if (bold !== null) {
      nodes.push(createElement('strong', { key: `${keyPrefix}-${key++}` }, bold[1]));
      rest = rest.slice(bold[0].length);
      continue;
    }

    const link = /^\[([^\]]+)\]\(([^)\s]+)\)/.exec(rest);
    if (link !== null) {
      const label = link[1];
      const destination = link[2];

      if (isSafeDestination(destination)) {
        nodes.push(
          createElement(
            'a',
            {
              key: `${keyPrefix}-${key++}`,
              href: destination,
              rel: 'noreferrer',
              'data-testid': 'review-preview-link',
            },
            label,
          ),
        );
      } else {
        // An unsafe destination is shown as the characters the author wrote, with no anchor.
        // A reader sees `[label](javascript:…)` as text and cannot reach it by clicking.
        pushText(link[0]);
      }

      rest = rest.slice(link[0].length);
      continue;
    }

    const footnote = /^\[\^([A-Za-z0-9_.-]+)\]/.exec(rest);
    if (footnote !== null) {
      nodes.push(
        createElement(
          'sup',
          { key: `${keyPrefix}-${key++}`, 'data-testid': 'review-preview-citation' },
          footnote[1],
        ),
      );
      rest = rest.slice(footnote[0].length);
      continue;
    }

    const next = nextSpecial(rest);
    pushText(rest.slice(0, next));
    rest = rest.slice(next);
  }

  return nodes;
}

/** Only http and https destinations are reachable. Everything else is text. */
export function isSafeDestination(destination: string): boolean {
  const trimmed = destination.trim();

  try {
    const parsed = new URL(trimmed, 'https://chronicleaf.invalid');

    // A relative reference parses against the base, and it is also safe: it names no host, no
    // scheme and no credentials, so it cannot reach anywhere from this page.
    if (!/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(trimmed)) {
      return true;
    }

    return (ALLOWED_PROTOCOLS as readonly string[]).includes(parsed.protocol);
  } catch {
    return false;
  }
}

function nextSpecial(text: string): number {
  const candidates = [
    text.indexOf('`'),
    text.indexOf('**'),
    text.indexOf('['),
  ].filter((at) => at >= 0);

  return candidates.length === 0 ? text.length : Math.min(...candidates);
}
