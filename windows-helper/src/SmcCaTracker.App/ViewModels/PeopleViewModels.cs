using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.App.ViewModels;

/// <summary>A Workload Tracker person offered in the People page's pickers.</summary>
public sealed record PersonOption(int Id, string Name, string? Detail)
{
    public string Label => string.IsNullOrWhiteSpace(Detail) ? Name : $"{Name} · {Detail}";

    public static PersonOption From(TeamMember member) =>
        new(member.PersonId, member.Name, string.Join(" · ", new[] { member.Role, member.Department }.Where(part => !string.IsNullOrWhiteSpace(part))));
}

public enum PersonLinkState
{
    /// <summary>Helix or Procore gives no id or email for them, so a link can't be kept.</summary>
    CantLink,
    NotLinked,

    /// <summary>A team member with a matching name, waiting for confirmation.</summary>
    Suggested,

    /// <summary>A person was picked here (not the suggestion, or not the one linked): waiting for confirmation.</summary>
    Changed,
    Linked
}

/// <summary>One Helix or Procore person on the People page, and the Workload Tracker person they are (or may be).</summary>
public sealed class PersonLinkRow : ObservableObject
{
    private CaPersonLink? link;
    private PersonOption? choice;
    private string? problem;
    private bool busy;

    public PersonLinkRow(ProviderPerson person, CaPersonLink? link, PersonOption? suggestion)
    {
        Person = person;
        this.link = link;
        Suggestion = suggestion;
    }

    public ProviderPerson Person { get; }

    /// <summary>The team member whose name matches, when the person isn't linked yet.</summary>
    public PersonOption? Suggestion { get; }

    public string Name => Person.Name;

    /// <summary>"SMC · pat@smc.example · 3 open items", and "you" for the person signed in to Helix or Procore.</summary>
    public string Detail => string.Join(" · ", new[]
    {
        Person.IsYou ? "You" : null,
        Person.Company,
        Person.Email,
        Person.OpenItems switch { 0 => null, 1 => "1 open item", var count => $"{count} open items" }
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>The person picked: the linked one, the suggestion, or another chosen here.</summary>
    public PersonOption? Choice
    {
        get => choice;
        set
        {
            if (Set(ref choice, value))
            {
                Problem = null;
                RaiseState();
            }
        }
    }

    public CaPersonLink? Link
    {
        get => link;
        set
        {
            link = value;
            RaiseState();
        }
    }

    public string? Problem
    {
        get => problem;
        set
        {
            if (Set(ref problem, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public bool Busy
    {
        get => busy;
        set
        {
            if (Set(ref busy, value))
            {
                RaiseState();
            }
        }
    }

    public int? LinkedPersonId => Link?.PersonId;

    public PersonLinkState State =>
        !Person.CanLink ? PersonLinkState.CantLink
        : LinkedPersonId is { } linked ? Choice is { } picked && picked.Id != linked ? PersonLinkState.Changed : PersonLinkState.Linked
        : Choice is null ? PersonLinkState.NotLinked
        : Choice == Suggestion ? PersonLinkState.Suggested
        : PersonLinkState.Changed;

    /// <summary>Their own sign-in links them automatically; that link isn't changed here.</summary>
    public bool IsProtected => Link?.LinkedBy == CaPersonLinkSourceEnum.Auto;

    public bool CanPick => Person.CanLink && !IsProtected && !Busy;

    public bool CanConfirm => !Busy && State is PersonLinkState.Suggested or PersonLinkState.Changed;

    public bool CanUnlink => !Busy && Link?.LinkedBy == CaPersonLinkSourceEnum.Manual && State == PersonLinkState.Linked;

    public string StateText => State switch
    {
        PersonLinkState.CantLink => "Can't be linked: Helix or Procore gives no id or email for them",
        PersonLinkState.NotLinked => "Not linked",
        PersonLinkState.Suggested => "Suggested by name: confirm it's them",
        PersonLinkState.Changed => LinkedPersonId is null ? "Picked here: confirm it's them" : "Changed: confirm",
        _ => Link?.LinkedBy switch
        {
            CaPersonLinkSourceEnum.Auto => "Linked: their own sign-in in CA Tracker",
            CaPersonLinkSourceEnum.Email => "Linked by matching email",
            _ => Link?.LinkedByName is { Length: > 0 } by ? $"Linked by {by}" : "Linked"
        } + (Link?.PersonActive == false ? " · has left" : "")
    };

    /// <summary>"good" (linked), "attention" (waiting for confirmation) or "" for the state's colour.</summary>
    public string StateKind => State switch
    {
        PersonLinkState.Linked => "good",
        PersonLinkState.Suggested or PersonLinkState.Changed => "attention",
        _ => ""
    };

    private void RaiseState()
    {
        foreach (var name in new[]
                 {
                     nameof(Link), nameof(LinkedPersonId), nameof(State), nameof(IsProtected), nameof(CanPick), nameof(CanConfirm),
                     nameof(CanUnlink), nameof(StateText), nameof(StateKind)
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
