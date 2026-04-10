# Project Context

- **Owner:** Chris Daly
- **Project:** eShop (AdventureWorks) — A reference .NET application implementing an e-commerce website using a services-based architecture with .NET Aspire.
- **Stack:** .NET 9, C#, .NET Aspire, Blazor, RabbitMQ, Docker, Entity Framework
- **Architecture:** Microservices — Basket.API, Catalog.API, Identity.API, Ordering.API, Webhooks.API, OrderProcessor, PaymentProcessor, EventBus
- **Frontend:** Blazor WebApp, WebAppComponents, ClientApp, HybridApp (MAUI)
- **Created:** 2026-04-10

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Initial Frontend Architecture Analysis (2026-04-10)

**WebApp (Blazor Server)** - `src/WebApp/`
- **Architecture**: Blazor Server with Interactive Server Components (.NET 9)
- **Authentication**: OpenID Connect with Identity.API, cookie-based sessions
- **Routes/Pages**:
  - `/` - Catalog home page with product listing and filters
  - `/item/{id}` - Item detail page with add-to-cart
  - `/cart` - Shopping basket with quantity updates
  - `/checkout` - Checkout form with address validation
  - `/user/orders` - Order history (requires auth)
  - `/user/logout` - Logout handler
- **API Integration**:
  - Basket.API via gRPC (BasketClient)
  - Catalog.API via HTTP (CatalogService)
  - Ordering.API via HTTP (OrderingService)
  - Auth tokens automatically attached via HTTP client interceptors
- **State Management**: Scoped BasketState with change notification pattern, OrderStatusNotificationService for real-time updates via EventBus
- **Layout**: MainLayout with HeaderBar (nav/user/cart menus), FooterBar, dynamic hero images
- **AI Integration**: Optional chatbot (Chatbot.razor) using Microsoft.Extensions.AI with Ollama or OpenAI, includes function invocation

**WebAppComponents (Shared Library)** - `src/WebAppComponents/`
- **Purpose**: Reusable Razor components shared between WebApp and HybridApp
- **Components**:
  - `CatalogListItem.razor` - Product card with image, name, price
  - `CatalogSearch.razor` - Filter UI by brand/type with active state styling
- **Services**:
  - `CatalogService.cs` - HTTP client for Catalog.API
  - `IProductImageUrlProvider` - Abstraction for product image URLs
- **Patterns**: Parameter-based components with EditorRequired attributes, scoped CSS files

**ClientApp (MAUI Native)** - `src/ClientApp/`
- **Architecture**: .NET MAUI cross-platform app (iOS, Android, Windows, macOS)
- **Pattern**: MVVM with CommunityToolkit.Mvvm (ObservableObject, RelayCommand)
- **ViewModels**: CatalogViewModel, BasketViewModel, CheckoutViewModel, OrderDetailViewModel, ProfileViewModel, LoginViewModel, MapViewModel
- **Views**: XAML-based with templates, custom controls, converters, triggers, and effects
- **Services**: Mock/real service switching via AppEnvironmentService, Identity with OIDC browser flow
- **Fonts**: Montserrat and FontAwesome
- **Features**: App actions, maps integration (non-Windows), badge counts, filtering

**HybridApp (MAUI Blazor Wrapper)** - `src/HybridApp/`
- **Architecture**: MAUI with BlazorWebView, wraps Blazor components in native shell
- **Rendering**: Routes to Blazor components, shares WebAppComponents library
- **API**: Mobile BFF pattern with localhost/emulator routing (10.0.2.2 for Android)
- **Layout**: Simplified MainLayout (no chatbot), uses same HeaderBar/FooterBar/styling
- **Purpose**: Native app packaging of web UI with platform integration

**CSS/Styling**
- **Typography**: "Plus Jakarta Sans" (WebApp), "Montserrat" (ClientApp), "Open Sans" fallback
- **Pattern**: Scoped CSS per component (`.razor.css` files) + global `app.css`
- **Design System**: Black/white primary colors, button variants (primary/secondary), cart badges, status indicators
- **Images**: WebP hero images, SVG icons, product images via forwarder to Catalog.API
- **Responsive**: CSS Grid for catalog items, flexbox for layouts

**Frontend Testing** - `tests/ClientApp.UnitTests/`
- **Framework**: MSTest with mock services
- **Pattern**: ViewModel-focused unit tests
- **Mocks**: BasketMockService, CatalogMockService, OrderMockService, IdentityMockService
- **Coverage**: Command existence, property initialization, async initialization, property change notifications, filter clearing

### Product Recommendations Frontend (2026-04-10)

**Created:**
- `src/WebAppComponents/Catalog/ProductRecommendations.razor` — Reusable horizontal carousel component with [EditorRequired] CurrentItemId, MaxItems param (default 10). Fetches from CatalogService.GetRecommendations(), filters out current item client-side, fails silently on errors.
- `src/WebAppComponents/Catalog/ProductRecommendations.razor.css` — Scoped CSS using flex + overflow-x: auto + scroll-snap-type for horizontal scrolling. Cards have hover translateY(-4px) + box-shadow effect. Price in red (#d32f2f). Responsive breakpoint at 480px.

**Modified:**
- `src/WebAppComponents/Services/ICatalogService.cs` — Added `RecordProductView(int itemId)` and `GetRecommendations(int pageIndex, int pageSize)` method signatures.
- `src/WebAppComponents/Services/CatalogService.cs` — Implemented both methods: POST to `recommendations/view` with JSON body, GET from `recommendations?pageIndex=&pageSize=`.
- `src/WebApp/Components/Pages/Item/ItemPage.razor` — Added ProductRecommendations component below product details (isLoggedIn guard), added OnAfterRenderAsync for fire-and-forget RecordProductView on first render.

**Patterns followed:**
- Used `ItemHelper.Url(item)` for product links (matching CatalogListItem pattern)
- Used `[Parameter, EditorRequired]` for required params (matching CatalogListItem pattern)
- Used `data-enhance-nav="false"` on product links (matching CatalogListItem pattern)
- Injected `IProductImageUrlProvider` for image URLs (matching existing pattern)
- Service methods follow existing URL construction and async/await patterns in CatalogService

### 2026-04-10: Recommendations Feature Frontend Implementation

**Team Work - Frontend Developer**
Built ProductRecommendations carousel component and integrated recommendations display into ItemPage, following Rusty's architecture and coordinating with Linus (backend) and Basher (testing).

**Component Development:**
- ProductRecommendations.razor — Reusable horizontal carousel with [EditorRequired] CurrentItemId parameter, MaxItems config (default 10), automatic current-item filtering, silent error handling
- CSS with flexbox + overflow-x: auto + scroll-snap-type for smooth horizontal scrolling
- Hover effects: translateY(-4px) with box-shadow, price highlight (#d32f2f)
- Responsive breakpoint at 480px for mobile optimization
- Component designed for reuse in HybridApp (MAUI Blazor wrapper)

**Service Layer Updates:**
- ICatalogService.cs — Added two method signatures: RecordProductView(int itemId), GetRecommendations(int pageIndex, int pageSize)
- CatalogService.cs — Implemented POST to /api/v1.0/recommendations/view with JSON body, GET from /api/v1.0/recommendations with query parameters
- URL construction and async/await follow existing CatalogService patterns

**Page Integration:**
- ItemPage.razor — Added ProductRecommendations component below product details with isLoggedIn guard
- OnAfterRenderAsync hook for fire-and-forget RecordProductView call on first render
- Navigation links use ItemHelper.Url() matching CatalogListItem pattern
- Image URLs via IProductImageUrlProvider abstraction

**Patterns:**
- [Parameter, EditorRequired] for component required parameters (CatalogListItem style)
- data-enhance-nav="false" on internal product links
- Fire-and-forget view recording to avoid UX blocking
- Component error resilience with silent failures

**Outcome:** Frontend integrated and committed, builds clean, ready for end-to-end testing.
