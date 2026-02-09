
import re
import sys

def parse_results(filename):
    with open(filename, 'r') as f:
        content = f.read()

    results = {}
    current_node_config = None
    current_scenario = None
    
    lines = content.splitlines()
    for line in lines:
        line = line.strip()
        if not line:
            continue
            
        if line.startswith("NODE CONFIGURATION:"):
            current_node_config = line.split(": ")[1].strip()
            if current_node_config not in results:
                results[current_node_config] = {}
            current_scenario = None # Reset scenario when config changes
            
        elif line.startswith("---"):
            # Format is usually "--- Scenario Name ---"
            # remove all dashes
            current_scenario = line.replace("-", "").strip()
            # If we have a config, ensure this scenario dict exists
            if current_node_config:
                if current_scenario not in results[current_node_config]:
                    results[current_node_config][current_scenario] = {}
                    
        elif line.startswith("==="):
            # Format is "=== Locator Name ==="
            current_locator = line.replace("=", "").strip()
            
            # Ensure we have the structure ready
            if current_node_config and current_scenario:
                if current_scenario not in results[current_node_config]:
                     results[current_node_config][current_scenario] = {}
                if current_locator not in results[current_node_config][current_scenario]:
                    results[current_node_config][current_scenario][current_locator] = {}
                    
        elif line.startswith("Solve Rate:"):
            # "Solve Rate: 95.0% (95/100)"
            match = re.search(r"([\d\.]+)%", line)
            if match and current_node_config and current_scenario and current_locator:
                results[current_node_config][current_scenario][current_locator]["solve_rate"] = float(match.group(1))
                
        elif line.startswith("Accurate <="):
            match = re.search(r"([\d\.]+)%", line)
            if match and current_node_config and current_scenario and current_locator:
                results[current_node_config][current_scenario][current_locator]["accurate"] = float(match.group(1))
                
        elif line.startswith("2D Error (X,Y):"):
            # "2D Error (X,Y): 6.86m (median 7.21m, std 3.38m)"
            match = re.search(r"([\d\.]+)m \(median ([\d\.]+)m, std ([\d\.]+)m\)", line)
            if match and current_node_config and current_scenario and current_locator:
                results[current_node_config][current_scenario][current_locator]["error_avg"] = float(match.group(1))
                results[current_node_config][current_scenario][current_locator]["error_median"] = float(match.group(2))
                results[current_node_config][current_scenario][current_locator]["error_std"] = float(match.group(3))

    return results

def compare(main_file, branch_file):
    main_results = parse_results(main_file)
    branch_results = parse_results(branch_file)

    print(f"| {'Config / Scenario / Locator':<60} | {'Metric':<15} | {'Main':<10} | {'Branch':<10} | {'Diff':<10} |")
    print(f"|{'-' * 62}|{'-' * 17}|{'-' * 12}|{'-' * 12}|{'-' * 12}|")

    for config, scenarios in main_results.items():
        if config not in branch_results:
            continue
        for scenario, locators in scenarios.items():
            if scenario not in branch_results[config]:
                continue
            for locator, metrics in locators.items():
                if locator not in branch_results[config][scenario]:
                    continue
                
                b_metrics = branch_results[config][scenario][locator]

                # Compare Median Error
                m_err = metrics.get('error_median')
                b_err = b_metrics.get('error_median')
                
                if m_err is not None and b_err is not None:
                    diff = b_err - m_err
                    # Only print significant changes (improvement > 0.1 or regression > 0.1)
                    if abs(diff) > 0.1: 
                        print(f"| {config[:15]} / {scenario[:15]} / {locator:<15} | {'Median Err':<15} | {m_err:<10.2f} | {b_err:<10.2f} | {diff:<10.2f} |")

                # Compare Solve Rate
                m_rate = metrics.get('solve_rate')
                b_rate = b_metrics.get('solve_rate')
                
                if m_rate is not None and b_rate is not None:
                    diff = b_rate - m_rate
                    if abs(diff) > 1.0:
                        print(f"| {config[:15]} / {scenario[:15]} / {locator:<15} | {'Solve Rate %':<15} | {m_rate:<10.1f} | {b_rate:<10.1f} | {diff:<10.1f} |")
                        
    # Also check if main has 0/low solve rate and branch has high
    print("\n### Notable Improvements (Solve Rate +20%)")
    for config, scenarios in branch_results.items():
        if config not in main_results: continue
        for scenario, locators in scenarios.items():
            if scenario not in main_results[config]: continue
            for locator, metrics in locators.items():
                if locator not in main_results[config][scenario]: continue
                
                b_rate = metrics.get('solve_rate', 0)
                m_rate = main_results[config][scenario][locator].get('solve_rate', 0)
                
                if b_rate - m_rate > 20.0:
                     print(f"- **IMPROVED**: {config} - {scenario} - {locator}: {m_rate}% -> {b_rate}%")

if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("Usage: python3 compare_sim_results.py <main_results> <branch_results>")
        sys.exit(1)
    compare(sys.argv[1], sys.argv[2])
