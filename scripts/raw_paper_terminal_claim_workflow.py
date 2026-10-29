"""Private pending-terminal API/UI/cold-retry and actual SIGKILL checks.

Use exported terminal-claims-v1 cash, stock or zero fixtures. The cash kill case
keeps one accepted pending claim and interrupts its later actual settlement.
"""
from raw_paper_terminal_workflow import main

if __name__ == '__main__':
    main(pending_claims=True)
