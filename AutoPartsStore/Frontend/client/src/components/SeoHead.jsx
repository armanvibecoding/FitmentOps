import { useEffect } from 'react';

const upsertMeta = (selector, attributes) => {
  let element = document.head.querySelector(selector);
  const created = !element;
  if (!element) {
    element = document.createElement('meta');
    document.head.appendChild(element);
  }
  const previousAttributes = Object.fromEntries(
    Object.keys(attributes).map((key) => [key, element.getAttribute(key)])
  );
  Object.entries(attributes).forEach(([key, value]) => element.setAttribute(key, value));
  return { element, created, previousAttributes };
};

const restoreMeta = ({ element, created, previousAttributes }) => {
  if (created) {
    element.remove();
    return;
  }
  Object.entries(previousAttributes).forEach(([key, value]) => {
    if (value == null) element.removeAttribute(key);
    else element.setAttribute(key, value);
  });
};

const SeoHead = ({ title, description, canonicalPath, structuredData }) => {
  useEffect(() => {
    const previousTitle = document.title;
    document.title = title;
    const descriptionElement = upsertMeta('meta[name="description"]', {
      name: 'description',
      content: description,
    });
    const robotsElement = upsertMeta('meta[name="robots"]', {
      name: 'robots',
      content: 'index,follow',
    });
    let canonical = document.head.querySelector('link[rel="canonical"]');
    const canonicalCreated = !canonical;
    const previousCanonicalHref = canonical?.getAttribute('href') ?? null;
    if (!canonical) {
      canonical = document.createElement('link');
      canonical.rel = 'canonical';
      document.head.appendChild(canonical);
    }
    canonical.href = new URL(canonicalPath, window.location.origin).href;
    let script = null;
    if (structuredData) {
      script = document.createElement('script');
      script.type = 'application/ld+json';
      script.dataset.fitmentOpsSeo = 'true';
      script.textContent = JSON.stringify(structuredData).replace(/</g, '\\u003c');
      document.head.appendChild(script);
    }
    return () => {
      document.title = previousTitle;
      restoreMeta(descriptionElement);
      restoreMeta(robotsElement);
      if (canonicalCreated) canonical.remove();
      else if (previousCanonicalHref == null) canonical.removeAttribute('href');
      else canonical.setAttribute('href', previousCanonicalHref);
      script?.remove();
    };
  }, [canonicalPath, description, structuredData, title]);

  return null;
};

export default SeoHead;
