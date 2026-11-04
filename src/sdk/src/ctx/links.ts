export interface StepLink {
  url: string;
  label?: string;
}

export const MAX_LINKS = 10;
export const MAX_LINK_URL = 2048;
export const MAX_LINK_LABEL = 100;

/** `ctx.link`, collecting into `links`. */
export function createLinks(): {
  links: StepLink[];
  link: (url: string, label?: string) => void;
} {
  const links: StepLink[] = [];
  const link = (url: string, label?: string) => {
    if (typeof url !== 'string' || !/^https?:\/\/[^/\s]/i.test(url) || !URL.canParse(url))
      throw new Error(`ctx.link: '${String(url)}' is not an absolute http or https URL`);
    if (url.length > MAX_LINK_URL)
      throw new Error(`ctx.link: the URL is longer than ${MAX_LINK_URL} characters`);
    if (label !== undefined && typeof label !== 'string')
      throw new Error('ctx.link: the label must be a string');
    if (label !== undefined && label.length > MAX_LINK_LABEL)
      throw new Error(`ctx.link: the label is longer than ${MAX_LINK_LABEL} characters`);
    if (links.length >= MAX_LINKS)
      throw new Error(`ctx.link: a step can add at most ${MAX_LINKS} links`);
    links.push(label ? { url, label } : { url });
  };
  return { links, link };
}
