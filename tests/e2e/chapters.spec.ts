import { test, expect } from './helpers';
import { createAndProcessBook } from './helpers';

const bookContent = Buffer.from(`# Chapter 1: Alpha
Content for chapter one about alpha.
## Chapter 2: Beta
Content for chapter two about beta.
## Chapter 3: Gamma
Content for chapter three about gamma.
`);

test.describe('Chapter Operations', () => {
  let slug: string;

  test.beforeAll(async () => {
    test.setTimeout(300_000);
    slug = await createAndProcessBook('Chapter Ops Test', bookContent);
  });

  test('navigate between chapters', async ({ page }) => {
    await page.goto(`/books/${slug}`);

    // Should have 3 chapters
    const chapters = page.locator('.chapter-item');
    await expect(chapters).toHaveCount(3, { timeout: 5000 });

    // Click chapter 2
    await chapters.nth(1).click();
    // Content should update (check for chapter 2 content)
    await expect(page.locator('.editor-content, .cm-content')).toContainText(/beta/i, { timeout: 5000 });

    // Click chapter 3
    await chapters.nth(2).click();
    await expect(page.locator('.editor-content, .cm-content')).toContainText(/gamma/i, { timeout: 5000 });
  });

  test('add new chapter', async ({ page }) => {
    await page.goto(`/books/${slug}`);

    // Type chapter title in add input
    const addInput = page.locator('.add-chapter input');
    await addInput.fill('Delta');
    await addInput.press('Enter');

    // New chapter should appear
    const chapters = page.locator('.chapter-item');
    await expect(chapters).toHaveCount(4, { timeout: 5000 });
    await expect(chapters.nth(3)).toContainText('Delta');
  });

  test('delete chapter', async ({ page }) => {
    await page.goto(`/books/${slug}`);

    const chapters = page.locator('.chapter-item');
    await expect(chapters).toHaveCount(3, { timeout: 5000 });

    // Hover over last chapter to reveal delete button
    const lastChapter = chapters.nth(2);
    await lastChapter.hover();
    await lastChapter.locator('.delete-btn').click();

    // Should now have 2 chapters
    await expect(page.locator('.chapter-item')).toHaveCount(2, { timeout: 5000 });
  });
});
