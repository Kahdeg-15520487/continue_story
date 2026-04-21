import { test, expect, createAndProcessBook } from './helpers';

const bookContent = Buffer.from(`# Chapter 1: The Beginning

It was a dark and stormy night. The old house on the hill had been abandoned for years.

Sarah approached cautiously, her flashlight cutting through the rain. The door was ajar.

## Chapter 2: The Discovery

Sarah climbed the stairs, each step creaking under her weight. At the top, she found a library.

Behind the door was a room full of books. In the center sat a large oak desk with an open journal.

## Chapter 3: The Secret

The journal described a device hidden in the house. A device that could capture shadows.

The last entry was incomplete. She looked around. Moonlight illuminated a section of the floor.
`);

test.describe('Upload & Conversion Flow', () => {
  test('upload txt file → book converts → chapters created → wiki generated', async ({ page }) => {
    test.setTimeout(300_000);
    const slug = await createAndProcessBook('Playwright Upload Test', bookContent);

    // Navigate to the book page
    await page.goto(`/books/${slug}`);

    // Book content should be visible in editor
    await expect(page.locator('.book-title')).toContainText('Playwright Upload Test');

    // Chapter sidebar should show chapters
    const chapterItems = page.locator('.chapter-item');
    await expect(chapterItems).toHaveCount(3, { timeout: 5000 });

    // Click Wiki button
    await page.getByRole('button', { name: /wiki/i }).click();

    // Wiki panel should have file tabs
    const wikiTabs = page.locator('.lore-tab');
    await expect(wikiTabs).toHaveCount(5, { timeout: 5000 });

    // Each tab should render markdown as HTML (not raw)
    const renderedHtml = page.locator('.lore-content');
    await expect(renderedHtml).toBeVisible();
  });
});
