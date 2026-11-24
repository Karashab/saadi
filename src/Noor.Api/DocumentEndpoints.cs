using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Noor.Domain;
using Npgsql;

namespace Noor.Api;

public static class DocumentEndpoints
{
    private const string JsonType = "application/json; charset=utf-8";

    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".dwg"] = "application/acad", [".dxf"] = "application/dxf",
        [".mpp"] = "application/vnd.ms-project", [".xer"] = "application/octet-stream",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff",
        [".zip"] = "application/zip", [".rar"] = "application/vnd.rar", [".7z"] = "application/x-7z-compressed",
        [".txt"] = "text/plain", [".csv"] = "text/csv",
    };

    private static readonly Dictionary<string, byte[]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "%PDF"u8.ToArray(), [".png"] = [0x89, 0x50, 0x4E, 0x47], [".jpg"] = [0xFF, 0xD8, 0xFF], [".jpeg"] = [0xFF, 0xD8, 0xFF],
        [".docx"] = [0x50, 0x4B], [".xlsx"] = [0x50, 0x4B], [".pptx"] = [0x50, 0x4B], [".zip"] = [0x50, 0x4B],
    };

    public sealed record CategoryRequest(string Name, Guid? ParentId, int? SortOrder);

    public static void MapNoorDocuments(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{projectId:guid}/document-categories", (Guid projectId, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            _ = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            return Json(await d.Scalar(
                """
                SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'parentId',parent_id,'name',name,'sortOrder',sort_order) ORDER BY sort_order, name),'[]')::text
                FROM document_categories WHERE project_id=@p
                """, ct, ("p", projectId)));
        }, ct));

        api.MapPost("/projects/{projectId:guid}/document-categories", (Guid projectId, CategoryRequest body, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            await RequireManager(d, projectId, ct);
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ApiFailure(400, "نام دسته لازم است.");
            if (body.ParentId is { } parent) await RequireCategory(d, projectId, parent, ct);
            if (await d.Scalar("SELECT 1 FROM document_categories WHERE project_id=@p AND parent_id IS NOT DISTINCT FROM @x::uuid AND name=@n", ct,
                    ("p", projectId), ("x", body.ParentId), ("n", body.Name)) is not null)
                throw new ApiFailure(409, "این دسته قبلاً وجود دارد.");
            var id = (Guid)(await d.Scalar(
                "INSERT INTO document_categories (project_id,parent_id,name,sort_order) VALUES (@p,@x::uuid,@n,@s) RETURNING id", ct,
                ("p", projectId), ("x", body.ParentId), ("n", body.Name), ("s", body.SortOrder ?? 100)))!;
            return Results.Json(new { id, name = body.Name }, statusCode: 201);
        }, ct));

        api.MapPatch("/projects/{projectId:guid}/document-categories/{id:guid}", (Guid projectId, Guid id, CategoryRequest body, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            await RequireManager(d, projectId, ct);
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ApiFailure(400, "نام دسته لازم است.");
            await RequireCategory(d, projectId, id, ct);
            if (await d.Scalar(
                    """
                    SELECT 1 FROM document_categories o JOIN document_categories c ON c.id=@i
                    WHERE o.project_id=@p AND o.id<>@i AND o.parent_id IS NOT DISTINCT FROM c.parent_id AND o.name=@n
                    """, ct, ("p", projectId), ("i", id), ("n", body.Name)) is not null)
                throw new ApiFailure(409, "این دسته قبلاً وجود دارد.");
            await d.Execute("UPDATE document_categories SET name=@n, sort_order=coalesce(@s::int, sort_order) WHERE id=@i", ct,
                ("n", body.Name), ("s", body.SortOrder), ("i", id));
            return Results.NoContent();
        }, ct));

        api.MapDelete("/projects/{projectId:guid}/document-categories/{id:guid}", (Guid projectId, Guid id, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            await RequireManager(d, projectId, ct);
            await RequireCategory(d, projectId, id, ct);
            if (await d.Scalar("SELECT 1 FROM documents WHERE category_id=@i LIMIT 1", ct, ("i", id)) is not null
                || await d.Scalar("SELECT 1 FROM document_categories WHERE parent_id=@i LIMIT 1", ct, ("i", id)) is not null)
                throw new ApiFailure(409, "این دسته خالی نیست.");
            await d.Execute("DELETE FROM document_categories WHERE id=@i", ct, ("i", id));
            return Results.NoContent();
        }, ct));

        api.MapGet("/projects/{projectId:guid}/documents", (Guid projectId, Guid? categoryId, Guid? taskId, string? q, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            await Require(d, projectId, null, Permission.ViewDocument, ct, anyScope: true);
            return Json(await d.Scalar(
                """
                SELECT coalesce(jsonb_agg(x.j ORDER BY x.created_at DESC),'[]')::text FROM (
                  SELECT d.created_at, jsonb_build_object('id',d.id,'title',d.title,'code',d.code,'categoryId',d.category_id,'taskId',d.task_id,
                    'organizationId',d.organization_id,'createdAt',d.created_at,
                    'latest',jsonb_build_object('id',v.id,'versionNo',v.version_no,'fileName',v.file_name,'sizeBytes',v.size_bytes,'uploadedAt',v.uploaded_at)) AS j
                  FROM documents d
                  JOIN LATERAL (SELECT * FROM document_versions dv WHERE dv.document_id=d.id ORDER BY dv.version_no DESC LIMIT 1) v ON true
                  WHERE d.project_id=@p AND d.deleted_at IS NULL
                    AND (@c::uuid IS NULL OR d.category_id=@c::uuid)
                    AND (@t::uuid IS NULL OR d.task_id=@t::uuid)
                    AND (@q::text IS NULL OR position(lower(@q::text) IN lower(d.title || ' ' || coalesce(d.code,'') || ' ' || v.file_name)) > 0)
                  ORDER BY d.created_at DESC LIMIT 500
                ) x
                """, ct, ("p", projectId), ("c", categoryId), ("t", taskId), ("q", string.IsNullOrWhiteSpace(q) ? null : q.Trim())));
        }, ct));

        api.MapPost("/projects/{projectId:guid}/documents", UploadDocument);
        api.MapPost("/documents/{documentId:guid}/versions", UploadVersion);

        api.MapGet("/documents/{documentId:guid}/versions", (Guid documentId, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            var doc = await LoadDocument(d, documentId, ct);
            await Require(d, doc.ProjectId, doc.OrganizationId, Permission.ViewDocument, ct);
            return Json(await d.Scalar(
                """
                SELECT coalesce(jsonb_agg(jsonb_build_object('id',v.id,'versionNo',v.version_no,'fileName',v.file_name,'sizeBytes',v.size_bytes,
                  'sha256',v.sha256,'uploadedBy',u.display_name,'uploadedAt',v.uploaded_at) ORDER BY v.version_no DESC),'[]')::text
                FROM document_versions v JOIN users u ON u.id=v.uploaded_by WHERE v.document_id=@i
                """, ct, ("i", documentId)));
        }, ct));

        api.MapGet("/document-versions/{versionId:guid}/download", DownloadVersion);

        api.MapDelete("/documents/{documentId:guid}", (Guid documentId, HttpContext http, NoorDb db, CancellationToken ct) => db.Run(http.User, async d =>
        {
            var doc = await LoadDocument(d, documentId, ct);
            await Require(d, doc.ProjectId, doc.OrganizationId, Permission.DeleteDocument, ct);
            if (await d.Execute("UPDATE documents SET deleted_at=now() WHERE id=@i AND deleted_at IS NULL", ct, ("i", documentId)) != 1)
                throw new ApiFailure(403, "دسترسی لازم را ندارید.");
            await d.Audit(doc.ProjectId, "document", documentId.ToString(), "delete", JsonSerializer.Serialize(new { doc.Title }), null, http.Connection.RemoteIpAddress, ct);
            return Results.NoContent();
        }, ct));
    }

    private static async Task<IResult> UploadDocument(Guid projectId, HttpContext http, NoorDb db, IFileStore store, IVirusScanner scanner, IConfiguration config, CancellationToken ct)
    {
        var (form, file, failure) = await ReadUpload(http, config, scanner, ct);
        if (failure is not null) return failure;
        return await WithFile(store, file!, ct, save => db.Run(http.User, async d =>
        {
            var member = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            var title = form!["title"].ToString();
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(SafeName(file!.FileName));
            var code = NullIfEmpty(form["code"]);
            var categoryId = ParseGuid(form["categoryId"], "دسته");
            var taskId = ParseGuid(form["taskId"], "تسک");
            var requestedOrg = ParseGuid(form["organizationId"], "شرکت");
            if (categoryId is { } category) await RequireCategory(d, projectId, category, ct);

            Guid? taskOrg = null;
            if (taskId is { } task)
            {
                await using var command = d.Command("SELECT organization_id FROM tasks WHERE id=@i AND project_id=@p AND deleted_at IS NULL", ("i", task), ("p", projectId));
                await using var r = await command.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) throw new ApiFailure(404, "تسک پیدا نشد.");
                taskOrg = r.IsDBNull(0) ? null : r.GetGuid(0);
            }
            var organizationId = AccessPolicy.HasProjectWideScope(member.Role) ? requestedOrg ?? taskOrg : member.OrganizationId;
            if (organizationId is { } org && await d.Scalar("SELECT 1 FROM project_organizations WHERE project_id=@p AND organization_id=@o", ct, ("p", projectId), ("o", org)) is null)
                throw new ApiFailure(400, "این شرکت به پروژه وصل نیست.");
            await Require(d, projectId, organizationId, Permission.UploadDocument, ct);

            var stored = await save();
            var id = (Guid)(await d.Scalar(
                """
                INSERT INTO documents (project_id, organization_id, category_id, task_id, code, title, created_by)
                VALUES (@p,@o::uuid,@c::uuid,@t::uuid,@code,@title,@u) RETURNING id
                """, ct, ("p", projectId), ("o", organizationId), ("c", categoryId), ("t", taskId), ("code", code), ("title", title), ("u", d.UserId)))!;
            var versionId = await InsertVersion(d, id, 1, stored, file!, ct);
            await d.Audit(projectId, "document", id.ToString(), "upload", null,
                JsonSerializer.Serialize(new { title, versionNo = 1, fileName = SafeName(file!.FileName), stored.Sha256 }), http.Connection.RemoteIpAddress, ct);
            return Results.Json(new { id, versionId, versionNo = 1 }, statusCode: 201);
        }, ct));
    }

    private static async Task<IResult> UploadVersion(Guid documentId, HttpContext http, NoorDb db, IFileStore store, IVirusScanner scanner, IConfiguration config, CancellationToken ct)
    {
        var (_, file, failure) = await ReadUpload(http, config, scanner, ct);
        if (failure is not null) return failure;
        return await WithFile(store, file!, ct, save => db.Run(http.User, async d =>
        {
            var doc = await LoadDocument(d, documentId, ct);
            await Require(d, doc.ProjectId, doc.OrganizationId, Permission.UploadDocument, ct);
            if (await d.Scalar("SELECT 1 FROM documents WHERE id=@i AND deleted_at IS NULL FOR UPDATE", ct, ("i", documentId)) is null)
                throw new ApiFailure(403, "دسترسی لازم را ندارید.");
            var next = (int)(await d.Scalar("SELECT coalesce(max(version_no),0)+1 FROM document_versions WHERE document_id=@i", ct, ("i", documentId)))!;
            var stored = await save();
            var versionId = await InsertVersion(d, documentId, next, stored, file!, ct);
            await d.Audit(doc.ProjectId, "document", documentId.ToString(), "new_version", null,
                JsonSerializer.Serialize(new { versionNo = next, fileName = SafeName(file!.FileName), stored.Sha256 }), http.Connection.RemoteIpAddress, ct);
            return Results.Json(new { id = documentId, versionId, versionNo = next }, statusCode: 201);
        }, ct));
    }

    private static Task<IResult> DownloadVersion(Guid versionId, HttpContext http, NoorDb db, IFileStore store, CancellationToken ct) =>
        db.Run(http.User, async d =>
        {
            await using var command = d.Command(
                """
                SELECT d.id, d.project_id, d.organization_id, v.storage_key, v.file_name, v.content_type
                FROM document_versions v JOIN documents d ON d.id=v.document_id
                WHERE v.id=@i AND d.deleted_at IS NULL
                """, ("i", versionId));
            Guid documentId, projectId; Guid? organizationId; string key, name, type;
            await using (var r = await command.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) throw new ApiFailure(404, "فایل پیدا نشد.");
                (documentId, projectId, organizationId, key, name, type) =
                    (r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5));
            }
            await Require(d, projectId, organizationId, Permission.ViewDocument, ct);
            Stream stream;
            try { stream = store.OpenRead(key); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new ApiFailure(404, "فایل در مخزن پیدا نشد."); }
            try
            {
                await d.Audit(projectId, "document", documentId.ToString(), "download", null,
                    JsonSerializer.Serialize(new { versionId }), http.Connection.RemoteIpAddress, ct);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            return Results.File(stream, type, name, enableRangeProcessing: true);
        }, ct);

    private static async Task<IResult> WithFile(IFileStore store, IFormFile file, CancellationToken ct, Func<Func<Task<StoredFile>>, Task<IResult>> action)
    {
        StoredFile? saved = null;
        IResult result;
        try
        {
            result = await action(async () =>
            {
                await using var input = file.OpenReadStream();
                return saved = await store.SaveAsync(input, ct);
            });
        }
        catch
        {
            if (saved is not null) store.Delete(saved.Key);
            throw;
        }
        if (saved is not null && result is IStatusCodeHttpResult { StatusCode: >= 400 }) store.Delete(saved.Key);
        return result;
    }

    private static async Task<(IFormCollection? Form, IFormFile? File, IResult? Failure)> ReadUpload(HttpContext http, IConfiguration config, IVirusScanner scanner, CancellationToken ct)
    {
        var limit = config.GetValue<long>("Storage:MaxFileBytes", 500L * 1024 * 1024);
        var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = limit + 1024 * 1024;
        if (!http.Request.HasFormContentType) return (null, null, NoorDb.Error(400, "فایل ارسال نشده است."));

        var form = await http.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = limit + 1024 * 1024 }, ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return (form, null, NoorDb.Error(400, "فایل خالی است."));
        if (file.Length > limit) return (form, null, NoorDb.Error(413, "حجم فایل بیشتر از حد مجاز است."));
        var extension = Path.GetExtension(SafeName(file.FileName));
        if (!AllowedTypes.ContainsKey(extension)) return (form, null, NoorDb.Error(415, "این نوع فایل مجاز نیست."));
        if (Signatures.TryGetValue(extension, out var signature))
        {
            await using var input = file.OpenReadStream();
            var head = new byte[signature.Length];
            var read = await input.ReadAtLeastAsync(head, head.Length, false, ct);
            if (read < head.Length || !head.SequenceEqual(signature))
                return (form, null, NoorDb.Error(415, "محتوای فایل با پسوند آن مطابقت ندارد."));
        }
        await using (var input = file.OpenReadStream())
        {
            var verdict = await scanner.ScanAsync(input, ct);
            if (verdict == ScanVerdict.Infected) return (form, null, NoorDb.Error(422, "این فایل آلوده تشخیص داده شد و پذیرفته نشد."));
            if (verdict == ScanVerdict.Unavailable && config.GetValue("Scan:Required", false))
                return (form, null, NoorDb.Error(503, "بررسی امنیتی فایل در دسترس نیست. کمی بعد دوباره تلاش کنید."));
        }
        return (form, file, null);
    }

    private static async Task<Guid> InsertVersion(RequestDb d, Guid documentId, int number, StoredFile stored, IFormFile file, CancellationToken ct)
    {
        var name = SafeName(file.FileName);
        return (Guid)(await d.Scalar(
            """
            INSERT INTO document_versions (document_id, version_no, storage_key, file_name, content_type, size_bytes, sha256, uploaded_by)
            VALUES (@d,@n,@k,@f,@t,@s,@h,@u) RETURNING id
            """, ct, ("d", documentId), ("n", number), ("k", stored.Key), ("f", name),
            ("t", AllowedTypes[Path.GetExtension(name)]), ("s", stored.Size), ("h", stored.Sha256), ("u", d.UserId)))!;
    }

    public static string SafeName(string fileName)
    {
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or ':' or '|' or '?' or '*')).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 200)
        {
            var extension = Path.GetExtension(name);
            name = name[..(200 - extension.Length)] + extension;
        }
        return name;
    }

    private sealed record DocumentState(Guid ProjectId, Guid? OrganizationId, string Title);

    private static async Task<DocumentState> LoadDocument(RequestDb d, Guid id, CancellationToken ct)
    {
        await using var command = d.Command("SELECT project_id, organization_id, title FROM documents WHERE id=@i AND deleted_at IS NULL", ("i", id));
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new ApiFailure(404, "مدرک پیدا نشد.");
        return new DocumentState(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2));
    }

    private static async Task<Membership> Require(RequestDb d, Guid projectId, Guid? organizationId, Permission permission, CancellationToken ct, bool anyScope = false)
    {
        var member = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
        if (!AccessPolicy.Can(member, permission, new(projectId, anyScope ? member.OrganizationId : organizationId)))
            throw new ApiFailure(403, "دسترسی لازم را ندارید.");
        return member;
    }

    private static async Task RequireManager(RequestDb d, Guid projectId, CancellationToken ct)
    {
        var member = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
        if (!AccessPolicy.HasProjectWideScope(member.Role)) throw new ApiFailure(403, "فقط مدیر پروژه یا مدیر سیستم مجاز است.");
    }

    private static async Task RequireCategory(RequestDb d, Guid projectId, Guid id, CancellationToken ct)
    {
        if (await d.Scalar("SELECT 1 FROM document_categories WHERE id=@i AND project_id=@p", ct, ("i", id), ("p", projectId)) is null)
            throw new ApiFailure(404, "دسته پیدا نشد.");
    }

    private static Guid? ParseGuid(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var id) ? id : throw new ApiFailure(400, $"{label} نامعتبر است.");
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IResult Json(object? value) => Results.Text((string)value!, JsonType);
}
