<script lang="ts">
  import '../app.css';

  let { children } = $props();
</script>

<div class="app-layout">
  {@render children()}
</div>

<style>
  :global(*) {
    box-sizing: border-box;
    margin: 0;
    padding: 0;
  }

  :global(body) {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    background: #0d1117;
    color: #c9d1d9;
  }

  .app-layout {
    display: flex;
    height: 100vh;
    width: 100vw;
  }
</style>
