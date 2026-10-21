using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.ViewModels;

public sealed record ProfileHierarchyNode(string Title, string Subtitle, string Label, int Depth, bool IsCurrent = false)
{
    public Thickness Indent => new(Math.Min(Depth, 3) * 14, 0, 0, 0);
}

public sealed record ProfileManager(string Name, string Position)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => x[0])).ToUpperInvariant();
}

/// <summary>A local projection of one authorized directory snapshot; rendering never fetches hierarchy nodes.</summary>
public sealed class ProfileOrganizationViewModel
{
    public string Organization { get; }
    public string Location { get; }
    public string Position { get; }
    public string Department { get; }
    public string Classification { get; }
    public string ManagerStatus { get; }
    public bool HasManagers => Managers.Count > 0;
    public bool ManagersHaveMore { get; }
    public IReadOnlyList<ProfileManager> Managers { get; }
    public IReadOnlyList<ProfileHierarchyNode> Departments { get; }
    public IReadOnlyList<ProfileHierarchyNode> Reporting { get; }
    public bool HasDepartments => Departments.Count > 0;
    public bool HasReporting => Reporting.Count > 0;

    private static string Join(params string?[] values) => string.Join(" · ", values.Where(x => !string.IsNullOrWhiteSpace(x)));
    private static string Grade(PositionName position) => Join(position.LevelName, string.IsNullOrWhiteSpace(position.Grade) ? null : "Grade " + position.Grade);
    public ProfileOrganizationViewModel(EmployeeOrganization detail, string name)
    {
        Organization = detail.Organization?.Name ?? "No current organization";
        Location = Join(detail.Organization?.CountryName, detail.Organization?.CityName);
        if (Location.Length == 0) Location = "Location not assigned";
        Position = detail.Position?.Title ?? "No current position assignment";
        Department = detail.Position?.UnitName ?? "No current department";
        Classification = detail.Position is {} position ? Grade(position) : "";
        if (Classification.Length == 0) Classification = "Level / grade not assigned";
        Managers = (detail.ReportingManagers ?? new()).Select(x => new ProfileManager(x.DisplayName, x.PositionTitle)).ToArray();
        ManagersHaveMore = detail.ReportingManagersHasMore;
        ManagerStatus = detail.ReportingPath.Count == 0 ? (detail.Position == null ? "No current position assignment" : "No reporting position")
            : detail.ReportingManagers == null ? "Manager names are unavailable" : "Reporting position is currently vacant";

        var departments = new List<ProfileHierarchyNode>();
        if (detail.Organization is {} organization) departments.Add(new(organization.Name, organization.Code ?? "", "Organization", 0));
        // Both backend paths start at the employee and run towards the root.
        foreach (var unit in detail.UnitPath.AsEnumerable().Reverse())
        {
            if (unit.Type == 0 && unit.OrganizationId == detail.Organization?.Id) continue;
            var current = detail.Position != null && unit.Id == detail.Position.UnitId;
            var kind = unit.Type switch { 0 => "Organization", 1 => "Division", 2 => "Department", 3 => "Section", 4 => "Office", 5 => "Team / Squad", _ => "Unit" };
            departments.Add(new(unit.Name, unit.Code ?? "", kind + (current ? " · Current department" : ""), departments.Count, current));
        }
        Departments = departments;
        var reporting = detail.ReportingPath.AsEnumerable().Reverse().Select((x, depth) => new ProfileHierarchyNode(x.Title, Join(x.UnitName, Grade(x)), "Reporting position", depth)).ToList();
        if (detail.Position is {} ownPosition) reporting.Add(new(ownPosition.Title, Join(name, ownPosition.UnitName, Grade(ownPosition)), "Current position", reporting.Count, true));
        Reporting = reporting;
    }
}
