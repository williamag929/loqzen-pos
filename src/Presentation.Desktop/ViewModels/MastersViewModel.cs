using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.Services;
using Core.Domain.Entities;

namespace Presentation.Desktop.ViewModels;

public enum MastersTab { Products = 0, Categories = 1, Taxes = 2, Customers = 3, Users = 4, Billers = 5 }

public partial class MastersViewModel : ObservableObject
{
    private readonly IProductService  _products;
    private readonly ICategoryService _categories;
    private readonly ITaxService      _taxes;
    private readonly ICustomerService _customers;
    private readonly IUserService     _users;
    private readonly IUserSession     _session;
    private readonly IBillPaymentService _billPayments;

    // ── Tab ─────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProductsTab))]
    [NotifyPropertyChangedFor(nameof(IsCategoriesTab))]
    [NotifyPropertyChangedFor(nameof(IsTaxesTab))]
    [NotifyPropertyChangedFor(nameof(IsCustomersTab))]
    [NotifyPropertyChangedFor(nameof(IsUsersTab))]
    [NotifyPropertyChangedFor(nameof(IsBillersTab))]
    private MastersTab _activeTab = MastersTab.Products;

    public bool IsProductsTab   => ActiveTab == MastersTab.Products;
    public bool IsCategoriesTab => ActiveTab == MastersTab.Categories;
    public bool IsTaxesTab      => ActiveTab == MastersTab.Taxes;
    public bool IsCustomersTab  => ActiveTab == MastersTab.Customers;

    // ── Collections ─────────────────────────────────────────────────────────
    public ObservableCollection<Product>  Products   { get; } = new();
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<Tax>      Taxes      { get; } = new();
    public ObservableCollection<Customer> Customers  { get; } = new();

    // ── Search ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _notification = string.Empty;
    [ObservableProperty] private bool _isBusy = false;

    // ── Product Edit Form ────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProductFormTitle))]
    private bool _isProductFormVisible = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProductFormTitle))]
    private bool _isEditingProduct = false;

    public string ProductFormTitle => IsEditingProduct ? "Editar Producto" : "Nuevo Producto";
    [ObservableProperty] private Guid _editProductId;
    [ObservableProperty] private string _productSku = string.Empty;
    [ObservableProperty] private string _productBarcode = string.Empty;
    [ObservableProperty] private string _productName = string.Empty;
    [ObservableProperty] private string _productDescription = string.Empty;
    [ObservableProperty] private string _productPriceStr = "0";
    [ObservableProperty] private string _productCostStr = "0";
    [ObservableProperty] private string _productStockStr = "0";
    [ObservableProperty] private bool _productIsWeighed = false;
    [ObservableProperty] private Category? _productCategory;
    [ObservableProperty] private Tax? _productTax;

    // ── Category Edit Form ───────────────────────────────────────────────────
    [ObservableProperty] private bool _isCategoryFormVisible = false;
    [ObservableProperty] private bool _isEditingCategory = false;
    [ObservableProperty] private Guid _editCategoryId;
    [ObservableProperty] private string _categoryName = string.Empty;
    [ObservableProperty] private string _categoryColorHex = "#3B82F6";
    [ObservableProperty] private int _categorySortOrder = 0;

    // ── Customer Edit Form ───────────────────────────────────────────────────
    [ObservableProperty] private bool _isCustomerFormVisible = false;
    [ObservableProperty] private bool _isEditingCustomer = false;
    [ObservableProperty] private Guid _editCustomerId;
    [ObservableProperty] private string _customerDocumentId = string.Empty;
    [ObservableProperty] private string _customerFullName = string.Empty;
    [ObservableProperty] private string _customerEmail = string.Empty;
    [ObservableProperty] private string _customerPhone = string.Empty;
    [ObservableProperty] private string _customerAddress = string.Empty;

    public MastersViewModel(
        IProductService products,
        ICategoryService categories,
        ITaxService taxes,
        ICustomerService customers,
        IUserService users,
        IUserSession session,
        IBillPaymentService billPayments)
    {
        _billPayments = billPayments;
        _products = products;
        _categories = categories;
        _taxes = taxes;
        _customers = customers;
        _users = users;
        _session = session;
        _session.Changed += OnSessionChangedForUsers;

        _ = LoadAllAsync();
    }

    // ── Tab Navigation ───────────────────────────────────────────────────────
    [RelayCommand] public void ShowProducts()   { ActiveTab = MastersTab.Products;   _ = LoadProductsAsync(); }
    [RelayCommand] public void ShowCategories() { ActiveTab = MastersTab.Categories; _ = LoadCategoriesAsync(); }
    [RelayCommand] public void ShowTaxes()      { ActiveTab = MastersTab.Taxes;      _ = LoadTaxesAsync(); }
    [RelayCommand] public void ShowCustomers()  { ActiveTab = MastersTab.Customers;  _ = LoadCustomersAsync(); }

    // ── Load All ─────────────────────────────────────────────────────────────
    public async Task LoadAllAsync()
    {
        await Task.WhenAll(LoadProductsAsync(), LoadCategoriesAsync(), LoadTaxesAsync(), LoadCustomersAsync(), LoadUsersAsync(), LoadBillersAsync());
    }

    private async Task LoadProductsAsync()
    {
        IsBusy = true;
        var items = await _products.GetAllAsync();
        Products.Clear();
        foreach (var i in items) Products.Add(i);
        IsBusy = false;
    }

    private async Task LoadCategoriesAsync()
    {
        var items = await _categories.GetAllAsync();
        Categories.Clear();
        foreach (var i in items) Categories.Add(i);
    }

    private async Task LoadTaxesAsync()
    {
        var items = await _taxes.GetAllAsync();
        Taxes.Clear();
        foreach (var i in items) Taxes.Add(i);
    }

    private async Task LoadCustomersAsync()
    {
        IsBusy = true;
        var items = await _customers.GetAllAsync();
        Customers.Clear();
        foreach (var i in items) Customers.Add(i);
        IsBusy = false;
    }

    // ── PRODUCTS CRUD ────────────────────────────────────────────────────────
    [RelayCommand]
    public void NewProduct()
    {
        IsEditingProduct   = false;
        EditProductId      = Guid.Empty;
        ProductSku         = string.Empty;
        ProductBarcode     = string.Empty;
        ProductName        = string.Empty;
        ProductDescription = string.Empty;
        ProductPriceStr    = "0";
        ProductCostStr     = "0";
        ProductStockStr    = "0";
        ProductIsWeighed   = false;
        ProductCategory    = null;
        ProductTax         = Taxes.FirstOrDefault();
        IsProductFormVisible = true;
    }

    [RelayCommand]
    public void EditProduct(Product product)
    {
        IsEditingProduct   = true;
        EditProductId      = product.Id;
        ProductSku         = product.Sku;
        ProductBarcode     = product.Barcode ?? string.Empty;
        ProductName        = product.Name;
        ProductDescription = product.Description ?? string.Empty;
        ProductPriceStr    = product.Price.ToString("0");
        ProductCostStr     = product.Cost.ToString("0");
        ProductStockStr    = product.StockQuantity.ToString("0.###");
        ProductIsWeighed   = product.IsWeighed;
        ProductCategory    = Categories.FirstOrDefault(c => c.Id == product.CategoryId);
        ProductTax         = Taxes.FirstOrDefault(t => t.Id == product.TaxId);
        IsProductFormVisible = true;
    }

    [RelayCommand]
    public async Task SaveProductAsync()
    {
        if (string.IsNullOrWhiteSpace(ProductName) || string.IsNullOrWhiteSpace(ProductSku))
        {
            Notification = "⚠️ Nombre y SKU son obligatorios.";
            return;
        }
        if (!decimal.TryParse(ProductPriceStr, out var price) || !decimal.TryParse(ProductCostStr, out var cost))
        {
            Notification = "⚠️ Precio o costo inválidos.";
            return;
        }
        decimal.TryParse(ProductStockStr, out var stock);

        try
        {
            if (IsEditingProduct)
            {
                var existing = await _products.GetByIdAsync(EditProductId);
                if (existing == null) return;
                existing.Sku          = ProductSku.Trim();
                existing.Barcode      = string.IsNullOrWhiteSpace(ProductBarcode) ? null : ProductBarcode.Trim();
                existing.Name         = ProductName.Trim();
                existing.Description  = string.IsNullOrWhiteSpace(ProductDescription) ? null : ProductDescription.Trim();
                existing.Price        = price;
                existing.Cost         = cost;
                existing.StockQuantity = stock;
                existing.IsWeighed    = ProductIsWeighed;
                existing.CategoryId   = ProductCategory?.Id;
                existing.TaxId        = ProductTax?.Id;
                await _products.UpdateAsync(existing);
                Notification = $"✅ Producto '{existing.Name}' actualizado.";
            }
            else
            {
                var product = new Product
                {
                    Sku          = ProductSku.Trim(),
                    Barcode      = string.IsNullOrWhiteSpace(ProductBarcode) ? null : ProductBarcode.Trim(),
                    Name         = ProductName.Trim(),
                    Description  = string.IsNullOrWhiteSpace(ProductDescription) ? null : ProductDescription.Trim(),
                    Price        = price,
                    Cost         = cost,
                    StockQuantity = stock,
                    IsWeighed    = ProductIsWeighed,
                    CategoryId   = ProductCategory?.Id,
                    TaxId        = ProductTax?.Id,
                    IsActive     = true
                };
                await _products.CreateAsync(product);
                Notification = $"✅ Producto '{product.Name}' creado.";
            }

            IsProductFormVisible = false;
            await LoadProductsAsync();
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DeleteProductAsync(Product product)
    {
        await _products.DeleteAsync(product.Id);
        Notification = $"🗑️ Producto '{product.Name}' eliminado.";
        await LoadProductsAsync();
    }

    [RelayCommand] public void CancelProductForm() => IsProductFormVisible = false;

    // ── CATEGORIES CRUD ───────────────────────────────────────────────────────
    [RelayCommand]
    public void NewCategory()
    {
        IsEditingCategory = false;
        EditCategoryId    = Guid.Empty;
        CategoryName      = string.Empty;
        CategoryColorHex  = "#3B82F6";
        CategorySortOrder = Categories.Count;
        IsCategoryFormVisible = true;
    }

    [RelayCommand]
    public void EditCategory(Category category)
    {
        IsEditingCategory = true;
        EditCategoryId    = category.Id;
        CategoryName      = category.Name;
        CategoryColorHex  = category.ColorHex ?? "#3B82F6";
        CategorySortOrder = category.SortOrder;
        IsCategoryFormVisible = true;
    }

    [RelayCommand]
    public async Task SaveCategoryAsync()
    {
        if (string.IsNullOrWhiteSpace(CategoryName))
        {
            Notification = "⚠️ El nombre de categoría es obligatorio.";
            return;
        }
        try
        {
            if (IsEditingCategory)
            {
                var existing = await _categories.GetByIdAsync(EditCategoryId);
                if (existing == null) return;
                existing.Name      = CategoryName.Trim();
                existing.ColorHex  = CategoryColorHex;
                existing.SortOrder = CategorySortOrder;
                await _categories.UpdateAsync(existing);
                Notification = $"✅ Categoría '{existing.Name}' actualizada.";
            }
            else
            {
                var cat = new Category { Name = CategoryName.Trim(), ColorHex = CategoryColorHex, SortOrder = CategorySortOrder };
                await _categories.CreateAsync(cat);
                Notification = $"✅ Categoría '{cat.Name}' creada.";
            }

            IsCategoryFormVisible = false;
            await LoadCategoriesAsync();
        }
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
    }

    [RelayCommand]
    public async Task DeleteCategoryAsync(Category category)
    {
        await _categories.DeleteAsync(category.Id);
        Notification = $"🗑️ Categoría '{category.Name}' eliminada.";
        await LoadCategoriesAsync();
    }

    [RelayCommand] public void CancelCategoryForm() => IsCategoryFormVisible = false;

    // ── CUSTOMERS CRUD ────────────────────────────────────────────────────────
    [RelayCommand]
    public void NewCustomer()
    {
        IsEditingCustomer  = false;
        EditCustomerId     = Guid.Empty;
        CustomerDocumentId = string.Empty;
        CustomerFullName   = string.Empty;
        CustomerEmail      = string.Empty;
        CustomerPhone      = string.Empty;
        CustomerAddress    = string.Empty;
        IsCustomerFormVisible = true;
    }

    [RelayCommand]
    public void EditCustomer(Customer customer)
    {
        IsEditingCustomer  = true;
        EditCustomerId     = customer.Id;
        CustomerDocumentId = customer.DocumentId;
        CustomerFullName   = customer.FullName;
        CustomerEmail      = customer.Email ?? string.Empty;
        CustomerPhone      = customer.PhoneNumber ?? string.Empty;
        CustomerAddress    = customer.Address ?? string.Empty;
        IsCustomerFormVisible = true;
    }

    [RelayCommand]
    public async Task SaveCustomerAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomerDocumentId) || string.IsNullOrWhiteSpace(CustomerFullName))
        {
            Notification = "⚠️ Documento y nombre son obligatorios.";
            return;
        }
        try
        {
            if (IsEditingCustomer)
            {
                var existing = await _customers.GetByDocumentIdAsync(CustomerDocumentId)
                            ?? Customers.FirstOrDefault(c => c.Id == EditCustomerId);
                if (existing == null) return;
                existing.DocumentId = CustomerDocumentId.Trim();
                existing.FullName   = CustomerFullName.Trim();
                existing.Email      = string.IsNullOrWhiteSpace(CustomerEmail) ? null : CustomerEmail.Trim();
                existing.PhoneNumber = string.IsNullOrWhiteSpace(CustomerPhone) ? null : CustomerPhone.Trim();
                existing.Address    = string.IsNullOrWhiteSpace(CustomerAddress) ? null : CustomerAddress.Trim();
                await _customers.UpdateAsync(existing);
                Notification = $"✅ Cliente '{existing.FullName}' actualizado.";
            }
            else
            {
                var customer = new Customer
                {
                    DocumentId  = CustomerDocumentId.Trim(),
                    FullName    = CustomerFullName.Trim(),
                    Email       = string.IsNullOrWhiteSpace(CustomerEmail) ? null : CustomerEmail.Trim(),
                    PhoneNumber = string.IsNullOrWhiteSpace(CustomerPhone) ? null : CustomerPhone.Trim(),
                    Address     = string.IsNullOrWhiteSpace(CustomerAddress) ? null : CustomerAddress.Trim(),
                    IsActive    = true
                };
                await _customers.CreateAsync(customer);
                Notification = $"✅ Cliente '{customer.FullName}' creado.";
            }
            IsCustomerFormVisible = false;
            await LoadCustomersAsync();
        }
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
    }

    [RelayCommand]
    public async Task DeleteCustomerAsync(Customer customer)
    {
        await _customers.DeleteAsync(customer.Id);
        Notification = $"🗑️ Cliente '{customer.FullName}' archivado.";
        await LoadCustomersAsync();
    }

    [RelayCommand] public void CancelCustomerForm() => IsCustomerFormVisible = false;
}
