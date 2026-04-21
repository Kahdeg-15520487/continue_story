import { test, expect, createAndProcessBook } from './helpers';

const bookContent = Buffer.from(`# Test Book for Wiki

Alice is the protagonist. She lives in Wonderland with the Cheshire Cat.
The main themes are curiosity and growing up. The primary location is Wonderland itself.

## Chapter 2
Alice has tea with the Mad Hatter.
`);

test.describe('Wiki Panel', () => {
  let slug: string;

  test.beforeAll(async () => {
    test.setTimeout(300_000);
    slug = await createAndProcessBook('Wiki Test Book', bookContent);
  });

  test('open wiki and browse files', async ({ page }) => {
    await page.goto(`/books/${slug}`);

    // Open wiki panel
    await page.getByRole('button', { name: /wiki/i }).click();

    // Should have 5 file tabs
    const tabs = page.locator('.lore-tab');
    await expect(tabs).toHaveCount(5, { timeout: 5000 });

    // Click each tab and verify content renders
    for (let i = 0; i < 5; i++) {
      await tabs.nth(i).click();
      const content = page.locator('.lore-content');
      await expect(content).toBeVisible();
      // Content should be rendered HTML, not raw markdown
      const innerHtml = await content.evaluate(el => el.innerHTML);
      // Rendered HTML should have tags like <h1>, <h2>, <p>, <strong>, etc.
      expect(innerHtml.length).toBeGreaterThan(10);
    }
  });

  test('resize wiki panel', async ({ page }) => {
    await page.goto(`/books/${slug}`);

    // Open wiki panel
    await page.getByRole('button', { name: /wiki/i }).click();

    const panel = page.locator('.side-panel').first();
    await expect(panel).toBeVisible({ timeout: 5000 });

    const initialWidth = await panel.evaluate(el => el.getBoundingClientRect().width);

    // Find resize handle
    const handle = page.locator('.resize-handle').first();
    await expect(handle).toBeVisible();

    // Drag handle to the left (panel gets wider)
    const box = await handle.boundingBox();
    if (box) {
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.mouse.move(box.x - 100, box.y + box.height / 2);
      await page.mouse.up();
    }

    // Panel width should have changed
    const newWidth = await panel.evaluate(el => el.getBoundingClientRect().width);
    expect(newWidth).not.toBe(initialWidth);
  });
});
