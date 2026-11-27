<script lang="ts">
  import ArrowLeft from 'lucide-svelte/icons/arrow-left';
  import ArrowUpDown from 'lucide-svelte/icons/arrow-up-down';
  import BookOpen from 'lucide-svelte/icons/book-open';
  import Brain from 'lucide-svelte/icons/brain';
  import Check from 'lucide-svelte/icons/check';
  import ChevronRight from 'lucide-svelte/icons/chevron-right';
  import Download from 'lucide-svelte/icons/download';
  import Lock from 'lucide-svelte/icons/lock';
  import Menu from 'lucide-svelte/icons/menu';
  import MessageSquare from 'lucide-svelte/icons/message-square';
  import MoreHorizontal from 'lucide-svelte/icons/more-horizontal';
  import NotebookText from 'lucide-svelte/icons/notebook-text';
  import Pencil from 'lucide-svelte/icons/pencil';
  import Plus from 'lucide-svelte/icons/plus';
  import RefreshCw from 'lucide-svelte/icons/refresh-cw';
  import Search from 'lucide-svelte/icons/search';
  import Send from 'lucide-svelte/icons/send';
  import Settings from 'lucide-svelte/icons/settings';
  import Sparkles from 'lucide-svelte/icons/sparkles';
  import Trash2 from 'lucide-svelte/icons/trash-2';
  import Upload from 'lucide-svelte/icons/upload';
  import X from 'lucide-svelte/icons/x';

  type IconName =
    | 'book'
    | 'back'
    | 'sort'
    | 'knowledge'
    | 'check'
    | 'chevron'
    | 'download'
    | 'lock'
    | 'menu'
    | 'chat'
    | 'more'
    | 'wiki'
    | 'edit'
    | 'plus'
    | 'refresh'
    | 'search'
    | 'send'
    | 'settings'
    | 'sparkles'
    | 'trash'
    | 'upload'
    | 'close';

  let {
    name,
    size = 20,
    strokeWidth = 1.75,
  }: { name: IconName; size?: number; strokeWidth?: number } = $props();
</script>

{#if name === 'book'}<BookOpen {size} {strokeWidth} />
{:else if name === 'back'}<ArrowLeft {size} {strokeWidth} />
{:else if name === 'sort'}<ArrowUpDown {size} {strokeWidth} />
{:else if name === 'knowledge'}<Brain {size} {strokeWidth} />
{:else if name === 'check'}<Check {size} {strokeWidth} />
{:else if name === 'chevron'}<ChevronRight {size} {strokeWidth} />
{:else if name === 'download'}<Download {size} {strokeWidth} />
{:else if name === 'lock'}<Lock {size} {strokeWidth} />
{:else if name === 'menu'}<Menu {size} {strokeWidth} />
{:else if name === 'chat'}<MessageSquare {size} {strokeWidth} />
{:else if name === 'more'}<MoreHorizontal {size} {strokeWidth} />
{:else if name === 'wiki'}<NotebookText {size} {strokeWidth} />
{:else if name === 'edit'}<Pencil {size} {strokeWidth} />
{:else if name === 'plus'}<Plus {size} {strokeWidth} />
{:else if name === 'refresh'}<RefreshCw {size} {strokeWidth} />
{:else if name === 'search'}<Search {size} {strokeWidth} />
{:else if name === 'send'}<Send {size} {strokeWidth} />
{:else if name === 'settings'}<Settings {size} {strokeWidth} />
{:else if name === 'sparkles'}<Sparkles {size} {strokeWidth} />
{:else if name === 'trash'}<Trash2 {size} {strokeWidth} />
{:else if name === 'upload'}<Upload {size} {strokeWidth} />
{:else if name === 'close'}<X {size} {strokeWidth} />
{/if}
